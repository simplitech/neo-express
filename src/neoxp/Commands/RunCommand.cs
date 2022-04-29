using System;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using Neo.BlockchainToolkit.Models;
using Neo.BlockchainToolkit.Persistence;

namespace NeoExpress.Commands
{
    [Command("run", Description = "Run Neo-Express instance node")]
    class RunCommand
    {
        readonly IExpressFile expressFile;

        public RunCommand(IExpressFile expressFile)
        {
            this.expressFile = expressFile;
        }

        public RunCommand(CommandLineApplication app) : this(app.GetExpressFile())
        {
        }

        [Argument(0, Description = "Index of node to run")]
        internal int NodeIndex { get; init; } = 0;

        [Option(Description = "Time between blocks")]
        internal uint? SecondsPerBlock { get; }

        [Option(Description = "Discard blockchain changes on shutdown")]
        internal bool Discard { get; init; } = false;

        [Option(Description = "Enable contract execution tracing")]
        internal bool Trace { get; init; } = false;

        internal Task<int> OnExecuteAsync(CommandLineApplication app, CancellationToken token) => app.ExecuteAsync(this.ExecuteAsync, token);

        internal async Task ExecuteAsync(IFileSystem fileSystem, IConsole console, CancellationToken token)
        {
            var chain = expressFile.Chain;
            if (NodeIndex < 0 || NodeIndex >= chain.ConsensusNodes.Count)
            {
                throw new Exception("Invalid node index");
            }

            var node = chain.ConsensusNodes[NodeIndex];
            var nodePath = fileSystem.GetNodePath(node);
            if (!fileSystem.Directory.Exists(nodePath)) fileSystem.Directory.CreateDirectory(nodePath);

            var storageProvider = Discard
                ? RocksDbStorageProvider.OpenForDiscard(nodePath)
                : RocksDbStorageProvider.Open(nodePath);

            using var disposable = storageProvider as IDisposable ?? Nito.Disposables.NoopDisposable.Instance;
            await Node.NodeUtility.RunAsync(chain, storageProvider, node, Trace, console, SecondsPerBlock, token);
        }
    }
}
