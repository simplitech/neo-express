﻿using Neo;
using Neo.IO;
using Neo.Network.P2P.Payloads;
using Neo.Network.RPC;
using Neo.Network.RPC.Models;
using Neo.SmartContract;
using Neo.SmartContract.Manifest;
using Neo.VM;
using Neo.Wallets;
using NeoExpress.Abstractions.Models;
using NeoExpress.Neo3.Models;
using NeoExpress.Neo3.Node;
using NeoExpress.Neo3.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NeoExpress.Neo3
{
    public class BlockchainOperations
    {
        public ExpressChain CreateBlockchain(FileInfo output, int count, TextWriter writer, CancellationToken token = default)
        {
            if (File.Exists(output.FullName))
            {
                throw new ArgumentException($"{output.FullName} already exists", nameof(output));
            }

            if (count != 1 && count != 4 && count != 7)
            {
                throw new ArgumentException("invalid blockchain node count", nameof(count));
            }

            var chain = BlockchainOperations.CreateBlockchain(count);

            writer.WriteLine($"Created {count} node privatenet at {output.FullName}");
            writer.WriteLine("    Note: The private keys for the accounts in this file are are *not* encrypted.");
            writer.WriteLine("          Do not use these accounts on MainNet or in any other system where security is a concern.");

            return chain;
        }

        private static char GetHexValue(int i) {
            if (i<10) {
                return (char)(i + '0');
            }
    
            return (char)(i - 10 + 'A');
        }

        public byte[] ToScriptHashByteArray(ExpressWalletAccount account)
        {
            var devAccount = DevWalletAccount.FromExpressWalletAccount(account);
            return devAccount.ScriptHash.ToArray();
        }

        public void ResetNode(ExpressChain chain, int index)
        {
            Console.WriteLine(Neo.SmartContract.Native.NativeContract.NEO.Hash);
            Console.WriteLine(
                BitConverter.ToString(Neo.SmartContract.Native.NativeContract.NEO.Hash.ToArray()));
            if (index >= chain.ConsensusNodes.Count)
            {
                throw new ArgumentException(nameof(index));
            }

            var node = chain.ConsensusNodes[index];
            var folder = node.GetBlockchainPath();

            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }

        static ExpressChain CreateBlockchain(int count)
        {
            var wallets = new List<(DevWallet wallet, Neo.Wallets.WalletAccount account)>(count);

            ushort GetPortNumber(int index, ushort portNumber) => (ushort)((49000 + (index * 1000)) + portNumber);

            for (var i = 1; i <= count; i++)
            {
                var wallet = new DevWallet($"node{i}");
                var account = wallet.CreateAccount();
                account.IsDefault = true;
                wallets.Add((wallet, account));
            }

            var keys = wallets.Select(t => t.account.GetKey().PublicKey).ToArray();

            var contract = Neo.SmartContract.Contract.CreateMultiSigContract((keys.Length * 2 / 3) + 1, keys);

            foreach (var (wallet, account) in wallets)
            {
                var multiSigContractAccount = wallet.CreateAccount(contract, account.GetKey());
                multiSigContractAccount.Label = "MultiSigContract";
            }

            // 49152 is the first port in the "Dynamic and/or Private" range as specified by IANA
            // http://www.iana.org/assignments/port-numbers
            var nodes = new List<ExpressConsensusNode>(count);
            for (var i = 0; i < count; i++)
            {
                nodes.Add(new ExpressConsensusNode()
                {
                    TcpPort = GetPortNumber(i, 333),
                    WebSocketPort = GetPortNumber(i, 334),
                    RpcPort = GetPortNumber(i, 332),
                    Wallet = wallets[i].wallet.ToExpressWallet()
                });
            }

            return new ExpressChain()
            {
                Magic = ExpressChain.GenerateMagicValue(),
                ConsensusNodes = nodes,
            };
        }

        private const string GENESIS = "genesis";

        static bool EqualsIgnoreCase(string a, string b)
            => string.Equals(a, b, StringComparison.InvariantCultureIgnoreCase);

        public ExpressWallet CreateWallet(ExpressChain chain, string name)
        {
            bool IsReservedName()
            {
                if (EqualsIgnoreCase(GENESIS, name))
                    return true;

                foreach (var node in chain.ConsensusNodes)
                {
                    if (EqualsIgnoreCase(name, node.Wallet.Name))
                        return true;
                }

                return false;
            }

            if (IsReservedName())
            {
                throw new Exception($"{name} is a reserved name. Choose a different wallet name.");
            }

            var wallet = new DevWallet(name);
            var account = wallet.CreateAccount();
            account.IsDefault = true;
            return wallet.ToExpressWallet();
        }

        public async Task RunBlockchainAsync(ExpressChain chain, int index, uint secondsPerBlock, bool discard, TextWriter writer, CancellationToken cancellationToken)
        {
            if (index >= chain.ConsensusNodes.Count)
            {
                throw new ArgumentException(nameof(index));
            }

            var node = chain.ConsensusNodes[index];
            var folder = node.GetBlockchainPath();
            writer.WriteLine(folder);

            if (!NodeUtility.InitializeProtocolSettings(chain, secondsPerBlock))
            {
                throw new Exception("could not initialize protocol settings");
            }

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            // create a named mutex so that checkpoint create command
            // can detect if blockchain is running automatically
            var multiSigAccount = node.Wallet.Accounts.Single(a => a.IsMultiSigContract());
            using var mutex = new Mutex(true, multiSigAccount.ScriptHash);

            var storagePlugin = discard 
                ? (Neo.Plugins.Plugin)new CheckpointStorePlugin(folder) 
                : (Neo.Plugins.Plugin)new RocksDbStorePlugin(folder);
            await NodeUtility.RunAsync(storagePlugin.Name, node, writer, cancellationToken);
        }

        public async Task RunCheckpointAsync(ExpressChain chain, string checkPointArchive, uint secondsPerBlock, TextWriter writer, CancellationToken cancellationToken)
        {
            string checkpointTempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                if (!NodeUtility.InitializeProtocolSettings(chain, secondsPerBlock))
                {
                    throw new Exception("could not initialize protocol settings");
                }

                if (chain.ConsensusNodes.Count != 1)
                {
                    throw new ArgumentException("Checkpoint restore is only supported on single node express instances", nameof(chain));
                }

                var node = chain.ConsensusNodes[0];
                var multiSigAccount = node.Wallet.Accounts.Single(a => a.IsMultiSigContract());

                System.IO.Compression.ZipFile.ExtractToDirectory(checkPointArchive, checkpointTempPath);
                ValidateCheckpoint(checkpointTempPath, chain.Magic, multiSigAccount);

                // create a named mutex so that checkpoint create command
                // can detect if blockchain is running automatically
                using var mutex = new Mutex(true, multiSigAccount.ScriptHash);

                var storagePlugin = new CheckpointStorePlugin(checkpointTempPath);
                await NodeUtility.RunAsync(storagePlugin.Name, node, writer, cancellationToken);
            }
            finally
            {
                if (Directory.Exists(checkpointTempPath))
                {
                    Directory.Delete(checkpointTempPath, true);
                }
            }
        }

        public async Task CreateCheckpoint(ExpressChain chain, string checkPointFileName, TextWriter writer)
        {
            static bool NodeRunning(ExpressConsensusNode node)
            {
                // Check to see if there's a neo-express blockchain currently running
                // by attempting to open a mutex with the multisig account address for 
                // a name. If so, do an online checkpoint instead of offline.

                var multiSigAccount = node.Wallet.Accounts.Single(a => a.IsMultiSigContract());

                if (Mutex.TryOpenExisting(multiSigAccount.ScriptHash, out var _))
                {
                    return true;
                }

                return false;
            }

            if (File.Exists(checkPointFileName))
            {
                throw new ArgumentException("Checkpoint file already exists", nameof(checkPointFileName));
            }

            if (chain.ConsensusNodes.Count != 1)
            {
                throw new ArgumentException("Checkpoint create is only supported on single node express instances", nameof(chain));
            }

            var node = chain.ConsensusNodes[0];
            var folder = node.GetBlockchainPath();

            if (NodeRunning(node))
            {
                var uri = chain.GetUri();
                var rpcClient = new RpcClient(uri.ToString());
                await rpcClient.RpcSendAsync("expresscreatecheckpoint", checkPointFileName);
                writer.WriteLine($"Created {Path.GetFileName(checkPointFileName)} checkpoint online");
            }
            else 
            {
                var multiSigAccount = node.Wallet.Accounts.Single(a => a.IsMultiSigContract());
                using var db = RocksDbStore.Open(folder);
                CreateCheckpoint(db, checkPointFileName, chain.Magic, multiSigAccount);
                writer.WriteLine($"Created {Path.GetFileName(checkPointFileName)} checkpoint offline");
            }
        }

        internal 
        void CreateCheckpoint(RocksDbStore db, string checkPointFileName, long magic, ExpressWalletAccount account)
        {
            string tempPath;
            do
            {
                tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            }
            while (Directory.Exists(tempPath));

            Console.WriteLine(tempPath);
            try
            {
                db.SaveCheckpoint(tempPath);

                using (var stream = File.OpenWrite(GetAddressFilePath(tempPath)))
                using (var writer = new StreamWriter(stream))
                {
                    writer.WriteLine(magic);
                    writer.WriteLine(account.ScriptHash);
                }

                if (File.Exists(checkPointFileName))
                {
                    throw new InvalidOperationException(checkPointFileName + " checkpoint file already exists");
                }
                System.IO.Compression.ZipFile.CreateFromDirectory(tempPath, checkPointFileName);
            }
            finally
            {
                Directory.Delete(tempPath, true);
            }
        }

        private const string ADDRESS_FILENAME = "ADDRESS.neo-express";
        private const string CHECKPOINT_EXTENSION = ".nxp3-checkpoint";

        private static string GetAddressFilePath(string directory) =>
            Path.Combine(directory, ADDRESS_FILENAME);

        public string ResolveCheckpointFileName(string checkPointFileName)
        {
            checkPointFileName = string.IsNullOrEmpty(checkPointFileName)
                ? $"{DateTimeOffset.Now:yyyyMMdd-hhmmss}{CHECKPOINT_EXTENSION}"
                : checkPointFileName;

            if (!Path.GetExtension(checkPointFileName).Equals(CHECKPOINT_EXTENSION))
            {
                checkPointFileName = checkPointFileName + CHECKPOINT_EXTENSION;
            }

            return Path.GetFullPath(checkPointFileName);
        }

        public void RestoreCheckpoint(ExpressChain chain, string checkPointArchive, bool force)
        {
            string checkpointTempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            try
            {
                if (chain.ConsensusNodes.Count != 1)
                {
                    throw new ArgumentException("Checkpoint restore is only supported on single node express instances", nameof(chain));
                }

                var node = chain.ConsensusNodes[0];
                var blockchainDataPath = node.GetBlockchainPath();

                if (!force && Directory.Exists(blockchainDataPath))
                {
                    throw new Exception("You must specify force to restore a checkpoint to an existing blockchain.");
                }

                System.IO.Compression.ZipFile.ExtractToDirectory(checkPointArchive, checkpointTempPath);
                var multiSigAccount = node.Wallet.Accounts.Single(a => a.IsMultiSigContract());
                ValidateCheckpoint(checkpointTempPath, chain.Magic, multiSigAccount);

                var addressFile = GetAddressFilePath(checkpointTempPath);
                if (File.Exists(addressFile))
                {
                    File.Delete(addressFile);
                }

                if (Directory.Exists(blockchainDataPath))
                {
                    Directory.Delete(blockchainDataPath, true);
                }

                Directory.Move(checkpointTempPath, blockchainDataPath);
            }
            finally
            {
                if (Directory.Exists(checkpointTempPath))
                {
                    Directory.Delete(checkpointTempPath, true);
                }
            }
        }

        static void ValidateCheckpoint(string checkPointDirectory, long magic, ExpressWalletAccount account)
        {
            var addressFile = GetAddressFilePath(checkPointDirectory);
            if (!File.Exists(addressFile))
            {
                throw new Exception("Invalid Checkpoint");
            }

            long checkPointMagic;
            string scriptHash;
            using (var stream = File.OpenRead(addressFile))
            using (var reader = new StreamReader(stream))
            {
                checkPointMagic = long.Parse(reader.ReadLine() ?? string.Empty);
                scriptHash = reader.ReadLine() ?? string.Empty;
            }

            if (magic != checkPointMagic || scriptHash != account.ScriptHash)
            {
                throw new Exception("Invalid Checkpoint");
            }
        }

        static IEnumerable<ExpressWalletAccount> GetMultiSigAccounts(ExpressChain chain, string scriptHash)
        {
            return chain.ConsensusNodes
                .Select(n => n.Wallet)
                .Concat(chain.Wallets)
                .Select(w => w.Accounts.FirstOrDefault(a => a.ScriptHash == scriptHash))
                .Where(a => a != null);
        }

        static void AddSignatures(ExpressChain chain, TransactionManager tm, WalletAccount account)
        {
            IEnumerable<WalletAccount> GetMultiSigAccounts()
            {
                var scriptHash = Neo.Wallets.Helper.ToAddress(account.ScriptHash);
                return chain.ConsensusNodes
                    .Select(n => n.Wallet)
                    .Concat(chain.Wallets)
                    .Select(w => w.Accounts.FirstOrDefault(a => a.ScriptHash == scriptHash))
                    .Where(a => a != null)
                    .Select(DevWalletAccount.FromExpressWalletAccount);
            }

            if (account.IsMultiSigContract())
            {
                var signers = GetMultiSigAccounts();

                var publicKeys = signers.Select(s => s.GetKey()!.PublicKey).ToArray();
                var sigCount = account.Contract.ParameterList.Length;

                foreach (var signer in signers.Take(sigCount))
                {
                    var keyPair = signer.GetKey() ?? throw new Exception();
                    tm = tm.AddMultiSig(keyPair, sigCount, publicKeys);
                }
            }
            else
            {
                tm = tm.AddSignature(account.GetKey()!);
            }
        }

        // https://github.com/neo-project/docs/blob/release-neo3/docs/en-us/tooldev/sdk/transaction.md
        public UInt256 Transfer(ExpressChain chain, string asset, string quantity, ExpressWalletAccount sender, ExpressWalletAccount receiver)
        {
            if (!NodeUtility.InitializeProtocolSettings(chain))
            {
                throw new Exception("could not initialize protocol settings");
            }

            var uri = chain.GetUri();
            var rpcClient = new RpcClient(uri.ToString());

            var assetHash = NodeUtility.GetAssetId(asset);
            var amount = GetAmount();

            var devSender = DevWalletAccount.FromExpressWalletAccount(sender);
            var devReceiver = DevWalletAccount.FromExpressWalletAccount(receiver);

            var script = assetHash.MakeScript("transfer", devSender.ScriptHash, devReceiver.ScriptHash, amount);
            var cosigners = new[] { new Cosigner { Scopes = WitnessScope.CalledByEntry, Account = devSender.ScriptHash } };

            var tm = new TransactionManager(rpcClient, devSender.ScriptHash)
                .MakeTransaction(script, null, cosigners);

            AddSignatures(chain, tm, devSender);

            var tx = tm.Sign().Tx;

            return rpcClient.SendRawTransaction(tx);

            BigInteger GetAmount()
            {
                var nep5client = new Nep5API(rpcClient);
                if ("all".Equals(quantity, StringComparison.InvariantCultureIgnoreCase))
                {
                    return nep5client.BalanceOf(assetHash, sender.ScriptHash.ToScriptHash());
                }

                if (decimal.TryParse(quantity, out var value))
                {
                    var decimals = nep5client.Decimals(assetHash);
                    return Neo.Network.RPC.Utility.ToBigInteger(value, decimals);
                }

                throw new Exception("invalid quantity");
            }
        }

        // https://github.com/neo-project/docs/blob/release-neo3/docs/en-us/tooldev/sdk/contract.md
        // https://github.com/ProDog/NEO-Test/blob/master/RpcClientTest/Test_ContractClient.cs#L38
        public async Task<UInt256> DeployContract(ExpressChain chain, string contract, ExpressWalletAccount account)
        {
            if (!NodeUtility.InitializeProtocolSettings(chain))
            {
                throw new Exception("could not initialize protocol settings");
            }

            var uri = chain.GetUri();
            var rpcClient = new RpcClient(uri.ToString());

            WalletAccount devAccount = DevWalletAccount.FromExpressWalletAccount(account);

            var (nefFile, manifest) = await LoadContract(contract);
            var script = CreateDeployScript(nefFile, manifest);
            var tm = new TransactionManager(rpcClient, devAccount.ScriptHash)
                .MakeTransaction(script);

            AddSignatures(chain, tm, devAccount);
            var tx = tm.Sign().Tx;
            return rpcClient.SendRawTransaction(tx);

            static async Task<(NefFile nefFile, ContractManifest manifest)> LoadContract(string contractPath)
            {
                var nefTask = Task.Run(() =>
                {
                    using var stream = File.OpenRead(contractPath);
                    using var reader = new BinaryReader(stream, Encoding.UTF8, false);
                    return Neo.IO.Helper.ReadSerializable<NefFile>(reader);
                });

                var manifestTask = File.ReadAllBytesAsync(Path.ChangeExtension(contractPath, ".manifest.json"))
                    .ContinueWith(t => ContractManifest.Parse(t.Result), TaskContinuationOptions.OnlyOnRanToCompletion);

                await Task.WhenAll(nefTask, manifestTask).ConfigureAwait(false);
                return (nefTask.Result, manifestTask.Result);
            }

            static byte[] CreateDeployScript(NefFile nefFile, ContractManifest manifest)
            {
                using var sb = new ScriptBuilder();
                sb.EmitSysCall(InteropService.Contract.Create, nefFile.Script, manifest.ToString());
                return sb.ToArray();
            }
        }

        static ContractParameter ParseArg(string arg)
        {
            if (arg.StartsWith("@N"))
            {
                var hash = Neo.Wallets.Helper.ToScriptHash(arg.Substring(1));
                return new ContractParameter()
                {
                    Type = ContractParameterType.Hash160,
                    Value = hash
                };
            }

            if (arg.StartsWith("0x")
                && BigInteger.TryParse(arg.AsSpan().Slice(2), System.Globalization.NumberStyles.HexNumber, null, out var bigInteger))
            {
                return new ContractParameter()
                {
                    Type = ContractParameterType.Integer,
                    Value = bigInteger
                };
            }

            return new ContractParameter()
            {
                Type = ContractParameterType.String,
                Value = arg
            };
        }

        static ContractParameter ParseArg(JToken arg)
        {
            return arg.Type switch
            {
                JTokenType.String => ParseArg(arg.Value<string>()),
                JTokenType.Boolean => new ContractParameter()
                {
                    Type = ContractParameterType.Boolean,
                    Value = arg.Value<bool>()
                },
                JTokenType.Integer => new ContractParameter()
                {
                    Type = ContractParameterType.Integer,
                    Value = new BigInteger(arg.Value<int>())
                },
                JTokenType.Array => new ContractParameter()
                {
                    Type = ContractParameterType.Array,
                    Value = ((JArray)arg).Select(ParseArg).ToList(),
                },
                _ => throw new Exception()
            };
        }

        static IEnumerable<ContractParameter> ParseArgs(JToken? args)
            => args == null
                ? Enumerable.Empty<ContractParameter>()
                : args.Select(ParseArg);

        static async Task<byte[]> LoadInvocationFileScript(string invocationFilePath)
        {
            JObject json;
            {
                using var fileStream = File.OpenRead(invocationFilePath);
                using var textReader = new StreamReader(fileStream);
                using var jsonReader = new JsonTextReader(textReader);
                json = await JObject.LoadAsync(jsonReader).ConfigureAwait(false);
            }

            var scriptHash = UInt160.Parse(json.Value<string>("hash"));
            var operation = json.Value<string>("operation");
            var args = ParseArgs(json.GetValue("args")).ToArray();

            using var sb = new ScriptBuilder();
            sb.EmitAppCall(scriptHash, operation, args);
            return sb.ToArray();
        }

        public async Task<UInt256> InvokeContract(ExpressChain chain, string invocationFilePath, ExpressWalletAccount account)
        {
            if (!NodeUtility.InitializeProtocolSettings(chain))
            {
                throw new Exception("could not initialize protocol settings");
            }

            var uri = chain.GetUri();
            var rpcClient = new RpcClient(uri.ToString());
            var script = await LoadInvocationFileScript(invocationFilePath);

            var devAccount = DevWalletAccount.FromExpressWalletAccount(account);
            var cosigners = new[] { new Cosigner { Scopes = WitnessScope.CalledByEntry, Account = devAccount.ScriptHash } };

            var tm = new TransactionManager(rpcClient, devAccount.ScriptHash)
                .MakeTransaction(script, null, cosigners);

            AddSignatures(chain, tm, devAccount);

            var tx = tm.Sign().Tx;

            return rpcClient.SendRawTransaction(tx);
        }

        public async Task<RpcInvokeResult> TestInvokeContract(ExpressChain chain, string invocationFilePath)
        {
            if (!NodeUtility.InitializeProtocolSettings(chain))
            {
                throw new Exception("could not initialize protocol settings");
            }

            var uri = chain.GetUri();
            var rpcClient = new RpcClient(uri.ToString());
            var script = await LoadInvocationFileScript(invocationFilePath);
            return rpcClient.InvokeScript(script);
        }
        
        public ExpressWalletAccount? GetAccount(ExpressChain chain, string name)
        {
            var wallet = (chain.Wallets ?? Enumerable.Empty<ExpressWallet>())
                .SingleOrDefault(w => name.Equals(w.Name, StringComparison.InvariantCultureIgnoreCase));
            if (wallet != null)
            {
                return wallet.DefaultAccount;
            }

            var node = chain.ConsensusNodes
                .SingleOrDefault(n => name.Equals(n.Wallet.Name, StringComparison.InvariantCultureIgnoreCase));
            if (node != null)
            {
                return node.Wallet.DefaultAccount;
            }

            if (GENESIS.Equals(name, StringComparison.InvariantCultureIgnoreCase))
            {
                return chain.ConsensusNodes
                    .Select(n => n.Wallet.Accounts.Single(a => a.IsMultiSigContract()))
                    .FirstOrDefault();
            }

            return null;
        }

        public async Task<BigInteger> ShowBalance(ExpressChain chain, ExpressWalletAccount account, string asset)
        {
            var uri = chain.GetUri();
            var nep5client = new Nep5API(new RpcClient(uri.ToString()));

            var assetHash = NodeUtility.GetAssetId(asset);

            await Task.CompletedTask;
            return nep5client.BalanceOf(assetHash, account.ScriptHash.ToScriptHash());
        }

        public async Task<RpcTransaction> ShowTransaction(ExpressChain chain, string txHash)
        {
            var uri = chain.GetUri();
            var rpcClient = new RpcClient(uri.ToString());

            await Task.CompletedTask;
            return rpcClient.GetRawTransaction(txHash);
        }

        public async Task<IReadOnlyList<ExpressStorage>> GetStorages(ExpressChain chain, string scriptHash)
        {
            var uri = chain.GetUri();
            var rpcClient = new RpcClient(uri.ToString());

            var json = await Task.Run(() => rpcClient.RpcSend("expressgetcontractstorage", scriptHash))
                .ConfigureAwait(false);
        
            if (json != null && json is Neo.IO.Json.JArray array)
            {
                var storages = new List<ExpressStorage>(array.Count);
                foreach (var s in array)
                {
                    var storage = new ExpressStorage()
                    {
                        Key = s["key"].AsString(),
                        Value = s["value"].AsString(),
                        Constant = s["constant"].AsBoolean()
                    };
                    storages.Add(storage);
                }
                return storages;
            }

            return new List<ExpressStorage>(0);
        }
    }
}