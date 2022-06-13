using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using Neo.SmartContract.Native;
using Neo.VM;

namespace NeoExpress.Commands
{
    partial class PolicyCommand
    {
        [Command("isBlocked", "blocked", Description = "Unblock account for usage")]
        internal class IsBlocked
        {
            readonly IExpressChain chain;

            public IsBlocked(IExpressChain chain)
            {
                this.chain = chain;
            }

            public IsBlocked(CommandLineApplication app)
            {
                this.chain = app.GetExpressFile();
            }

            [Argument(0, Description = "Account to check block status of")]
            [Required]
            internal string ScriptHash { get; init; } = string.Empty;

            internal Task<int> OnExecuteAsync(CommandLineApplication app)
                => app.ExecuteAsync(this.ExecuteAsync);

            internal async Task ExecuteAsync(IConsole console)
            {
                using var expressNode = chain.GetExpressNode();
                var scriptHash = await PolicyCommand.ResolveScriptHashAsync(expressNode, ScriptHash);
                using var builder = new ScriptBuilder();
                builder.EmitDynamicCall(NativeContract.Policy.Hash, "isBlocked", scriptHash);
                var result = await expressNode.GetResultAsync(builder.ToArray()).ConfigureAwait(false);
                var isBlocked = result.Stack[0].GetBoolean();
                await console.Out.WriteLineAsync($"{ScriptHash} account is {(isBlocked ? "" : "not ")}blocked");
            }
        }
    }
}