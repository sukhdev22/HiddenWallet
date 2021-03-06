﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HiddenWallet.KeyManagement;
using HiddenWallet.Models;
using NBitcoin;
using Xunit;
using Xunit.Abstractions;
using System.Diagnostics;
using QBitNinja.Client;
using QBitNinja.Client.Models;
using HiddenWallet.FullSpv;

namespace HiddenWallet.Tests
{
	public class FullSpvWalletTests
	{
		private bool _fullyConnected;
		private bool _syncedOnce;
		[Theory]
		[InlineData("TestNet")]
		[InlineData("Main")]
		public async Task SyncingTestAsync(string networkString)
		{
			// load wallet
			Network network = networkString == "TestNet"? Network.TestNet:Network.Main;
			string path = $"Wallets/Empty{network}.json";
			const string password = "";
			Safe safe = File.Exists(path) ? await Safe.LoadAsync(password, path) : (await Safe.CreateAsync(password, path, network)).Safe;

            Debug.WriteLine($"Unique Safe ID: {safe.UniqueId}");

            // create walletjob
            WalletJob walletJob = new WalletJob();
            await walletJob.InitializeAsync(Helpers.SocksPortHandler, Helpers.ControlPortClient, safe);

			// note some event
			_fullyConnected = false;
			_syncedOnce = false;
			walletJob.ConnectedNodeCountChanged += WalletJob_ConnectedNodeCountChanged;
			walletJob.StateChanged += WalletJob_StateChanged;

			Assert.Empty(walletJob.SafeAccounts);
			Assert.Equal(0, walletJob.ConnectedNodeCount);
			Assert.Empty((await walletJob.GetTrackerAsync()).TrackedTransactions);
			Assert.Empty(await walletJob.GetSafeHistoryAsync());
			Assert.Equal(WalletState.NotStarted, walletJob.State);
			Assert.True(walletJob.TracksDefaultSafe);

			// start syncing
			var cts = new CancellationTokenSource();
            var walletJobTask = walletJob.StartAsync(cts.Token);
            Assert.NotEqual(WalletState.NotStarted, walletJob.State);
			Task reportTask = Helpers.ReportAsync(cts.Token, walletJob);

			try
			{
				// wait until fully synced and connected
				while (!_fullyConnected)
				{
					await Task.Delay(10);
				}

				while (!_syncedOnce)
				{
                    await Task.Delay(1000);
				}

				Assert.Equal(WalletState.Synced, walletJob.State);
				Assert.NotEqual(Height.Unknown, await walletJob.GetCreationHeightAsync());
				Assert.Empty((await walletJob.GetTrackerAsync()).TrackedTransactions);
				Assert.Empty(await walletJob.GetSafeHistoryAsync());
                var headerHeightResult = await walletJob.TryGetHeaderHeightAsync();
                Assert.True(headerHeightResult.Success);
                var expectedBlockCount = headerHeightResult.Height.Value - (await walletJob.GetCreationHeightAsync()).Value + 1;
				Assert.Equal(expectedBlockCount, (await walletJob.GetTrackerAsync()).BlockCount);
				Assert.NotEmpty((await walletJob.GetTrackerAsync()).TrackedScriptPubKeys);
				Assert.Empty((await walletJob.GetTrackerAsync()).TrackedTransactions);
			}
			finally
			{
				cts?.Cancel();
                await Task.WhenAll(reportTask, walletJobTask);

                walletJob.ConnectedNodeCountChanged -= WalletJob_ConnectedNodeCountChanged;
				walletJob.StateChanged -= WalletJob_StateChanged;
                cts?.Dispose();
                reportTask?.Dispose();
                walletJobTask?.Dispose();
			}
		}

		private void WalletJob_StateChanged(object sender, EventArgs e)
		{
			var walletJob = sender as WalletJob;
			Debug.WriteLine($"{nameof(WalletJob.State)}: {walletJob.State}");
            _syncedOnce = walletJob.State == WalletState.Synced;
        }

		private void WalletJob_ConnectedNodeCountChanged(object sender, EventArgs e)
		{
            var walletJob = sender as WalletJob;
			if (walletJob.MaxConnectedNodeCount == walletJob.ConnectedNodeCount)
			{
				_fullyConnected = true;
				Debug.WriteLine(
					$"{nameof(walletJob.MaxConnectedNodeCount)} reached: {walletJob.MaxConnectedNodeCount}");
			}
			else Debug.WriteLine($"{nameof(walletJob.ConnectedNodeCount)}: {walletJob.ConnectedNodeCount}");
		}

		[Fact]
		public async Task HaveFundsTestAsync()
		{
			// load wallet
			Network network = Network.TestNet;
			string path = Path.Combine(Helpers.CommittedWalletsFolderPath, $"HaveFunds{network}.json");
			const string password = "";
			Safe safe = await Safe.LoadAsync(password, path);
			Assert.Equal(network, safe.Network);
			Debug.WriteLine($"Unique Safe ID: {safe.UniqueId}");

            // create walletjob
            WalletJob walletJob = new WalletJob();
            await walletJob.InitializeAsync(Helpers.SocksPortHandler, Helpers.ControlPortClient, safe);
            // note some event
            walletJob.ConnectedNodeCountChanged += WalletJob_ConnectedNodeCountChanged;
			walletJob.StateChanged += WalletJob_StateChanged;

			// start syncing
			var cts = new CancellationTokenSource();
			var walletJobTask = walletJob.StartAsync(cts.Token);
			Task reportTask = Helpers.ReportAsync(cts.Token, walletJob);

			try
			{
				// wait until synced enough to have our transaction
				while ((await walletJob.GetBestHeightAsync()).Type != HeightType.Chain || (await walletJob.GetBestHeightAsync()) < 1092448)
				{
                    await Task.Delay(1000);
				}

				var hasMoneyAddress = BitcoinAddress.Create("mmVZjqZjmLvxc3YFhWqYWoe5anrWVcoJcc");
				Debug.WriteLine($"Checking proper balance on {hasMoneyAddress.ToString()}");

				var record = (await walletJob.GetSafeHistoryAsync()).FirstOrDefault();
				Assert.NotEqual(default, record);

				Assert.True(record.Confirmed);
				Assert.Equal(new Money(0.1m, MoneyUnit.BTC), record.Amount);
                DateTimeOffset.TryParse("2017.03.06. 16:47:15 +00:00", out DateTimeOffset expTime);
                Assert.Equal(expTime, record.TimeStamp);
                var expectedId = new uint256("50898694f281ed059fa6b9d37ccf099ab261540be14fd43ce1a6d6684fbd4e94");
                Assert.Equal(expectedId, record.TransactionId);
			}
			finally
			{
				cts?.Cancel();
                await Task.WhenAll(reportTask, walletJobTask);

                walletJob.ConnectedNodeCountChanged -= WalletJob_ConnectedNodeCountChanged;
				walletJob.StateChanged -= WalletJob_StateChanged;
                cts?.Dispose();
                reportTask?.Dispose();
                walletJobTask?.Dispose();
			}
		}

		// test with a long time used testnet wallet, with exotic, including tumblebit transactions
		[Fact]
		public async Task RealHistoryTestAsync()
		{
			// load wallet
			Network network = Network.TestNet;
			string path = Path.Combine(Helpers.CommittedWalletsFolderPath, $"HiddenWallet.json");
			const string password = "";
			// I change it because I am using a very old wallet to test
			Safe.EarliestPossibleCreationTime = DateTimeOffset.ParseExact("2016-12-18", "yyyy-MM-dd", CultureInfo.InvariantCulture);
			Safe safe = await Safe.LoadAsync(password, path);
			Assert.Equal(network, safe.Network);
			Debug.WriteLine($"Unique Safe ID: {safe.UniqueId}");

            // create walletjob
            var walletJob = new WalletJob
            {
                MaxCleanAddressCount = 79
            };
            await walletJob.InitializeAsync(Helpers.SocksPortHandler, Helpers.ControlPortClient, safe);
            // note some event
            walletJob.ConnectedNodeCountChanged += WalletJob_ConnectedNodeCountChanged;
			_syncedOnce = false;
			walletJob.StateChanged += WalletJob_StateChanged;

			// start syncing
			var cts = new CancellationTokenSource();
			var walletJobTask = walletJob.StartAsync(cts.Token);
			Task reportTask = Helpers.ReportAsync(cts.Token, walletJob);

			try
			{
                // wait until fully synced
                while (!_syncedOnce)
                {
                    await Task.Delay(1000);
                }

                await Helpers.ReportFullHistoryAsync(walletJob);

                // 0. Query all operations, grouped our used safe addresses
                int MinUnusedKeyNum = 74;
                Dictionary<BitcoinAddress, List<BalanceOperation>> operationsPerAddresses = await Helpers.QueryOperationsPerSafeAddressesAsync(new QBitNinjaClient(safe.Network), safe, MinUnusedKeyNum);

				Dictionary<uint256, List<BalanceOperation>> operationsPerTransactions = QBitNinjaJutsus.GetOperationsPerTransactions(operationsPerAddresses);

				// 3. Create history records from the transactions
				// History records is arbitrary data we want to show to the user
				var txHistoryRecords = new List<(DateTimeOffset FirstSeen, Money Amount, int Confirmations, uint256 TxId)>();
				foreach (var elem in operationsPerTransactions)
				{
					var amount = Money.Zero;
					foreach (var op in elem.Value)
						amount += op.Amount;

					var firstOp = elem.Value.First();

					txHistoryRecords
						.Add((
							firstOp.FirstSeen,
							amount,
							firstOp.Confirmations,
							elem.Key));
				}

				// 4. Order the records by confirmations and time (Simply time does not work, because of a QBitNinja issue)
				var qBitHistoryRecords = txHistoryRecords
					.OrderByDescending(x => x.Confirmations) // Confirmations
					.ThenBy(x => x.FirstSeen); // FirstSeen

				var fullSpvHistoryRecords = await walletJob.GetSafeHistoryAsync();

				// This won't be equal QBit doesn't show us this transaction: 2017.01.04. 16:24:49	0.00000000	True		77b10ff78aab2e41764a05794c4c464922c73f0c23356190429833ce68fd7be9
				// Assert.Equal(qBitHistoryRecords.Count(), fullSpvHistoryRecords.Count());

				HashSet<WalletHistoryRecord> qBitFoundItToo = new HashSet<WalletHistoryRecord>();
				// Assert all record found by qbit also found by spv and they are identical
				foreach (var record in qBitHistoryRecords)
				{
					WalletHistoryRecord found = fullSpvHistoryRecords.FirstOrDefault(x => x.TransactionId == record.TxId);

                    Assert.NotEqual(default, found);
                    Assert.Equal(record.FirstSeen, found.TimeStamp);
                    Assert.Equal(record.Confirmations > 0, found.Confirmed);
                    Assert.Equal(record.Amount, found.Amount);
					qBitFoundItToo.Add(found);
				}

				foreach (var record in fullSpvHistoryRecords)
				{
					if (!qBitFoundItToo.Contains(record))
					{
                        Assert.DoesNotContain(qBitHistoryRecords, x => x.TxId == record.TransactionId);
						Debug.WriteLine($@"QBitNinja failed to find, but SPV found it: {record.TimeStamp.DateTime}	{record.Amount}	{record.Confirmed}		{record.TransactionId}");
					}
				}
			}
			finally
			{
				cts?.Cancel();
                await Task.WhenAll(reportTask, walletJobTask);

                walletJob.ConnectedNodeCountChanged -= WalletJob_ConnectedNodeCountChanged;
				walletJob.StateChanged -= WalletJob_StateChanged;
                cts?.Dispose();
                reportTask?.Dispose();
                walletJobTask?.Dispose();
			}
		}
	}
}
