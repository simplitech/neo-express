using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using Neo.Network.P2P.Payloads;
using Neo.Network.RPC;
using Neo.VM;
using TextWriter = System.IO.TextWriter;

namespace NeoExpress.Commands
{
    [Command("transfer", Description = "Transfer asset between accounts")]
    class TransferCommand
    {
        readonly IExpressChain chain;

        public TransferCommand(IExpressChain chain)
        {
            this.chain = chain;
        }

        public TransferCommand(CommandLineApplication app)
        {
            this.chain = app.GetExpressFile();
        }

        [Argument(0, Description = "Amount to transfer")]
        [Required]
        internal string Quantity { get; init; } = string.Empty;

        [Argument(1, Description = "Asset to transfer (symbol or script hash)")]
        [Required]
        internal string Asset { get; init; } = string.Empty;

        [Argument(2, Description = "Account to send asset from")]
        [Required]
        internal string Sender { get; init; } = string.Empty;

        [Argument(3, Description = "Account to send asset to")]
        [Required]
        internal string Receiver { get; init; } = string.Empty;

        [Option(Description = "password to use for NEP-2/NEP-6 sender")]
        internal string Password { get; init; } = string.Empty;

        [Option(Description = "Enable contract execution tracing")]
        internal bool Trace { get; init; } = false;

        [Option(Description = "Output as JSON")]
        internal bool Json { get; init; } = false;

        internal Task<int> OnExecuteAsync(CommandLineApplication app)
            => app.ExecuteAsync(this.ExecuteAsync);

        internal async Task ExecuteAsync(IConsole console)
        {
            using var expressNode = chain.GetExpressNode(Trace);
            var password = chain.ResolvePassword(Sender, Password);
            var txHash = await ExecuteAsync(expressNode, Quantity, Asset, Sender, password, Receiver)
                .ConfigureAwait(false);
            console.Out.WriteTxHash(txHash, "Transfer", Json);
        }

        public static async Task<Neo.UInt256> ExecuteAsync(IExpressNode expressNode, string quantity, 
            string asset, string sender, string password, string receiver, TextWriter? writer = null)
        {
            var (senderWallet, senderHash) = expressNode.Chain.ResolveSigner(sender, password);
            var receiverHash = expressNode.Chain.ResolveAccountHash(receiver);

            var assetHash = await expressNode.ParseAssetAsync(asset).ConfigureAwait(false);
            using var builder = new ScriptBuilder();
            if ("all".Equals(quantity, StringComparison.OrdinalIgnoreCase))
            {
                // balanceOf operation places current balance on eval stack
                builder.EmitDynamicCall(assetHash, "balanceOf", senderHash);
                // transfer operation takes 4 arguments, amount is 3rd parameter
                // push null onto the stack and then switch positions of the top
                // two items on eval stack so null is 4th arg and balance is 3rd
                builder.Emit(OpCode.PUSHNULL, OpCode.SWAP);
                builder.EmitPush(receiverHash);
                builder.EmitPush(senderHash);
                builder.EmitPush(4);
                builder.Emit(OpCode.PACK);
                builder.EmitPush("transfer");
                builder.EmitPush(asset);
                builder.EmitSysCall(Neo.SmartContract.ApplicationEngine.System_Contract_Call);
            }
            else if (decimal.TryParse(quantity, out var amount))
            {
                var decimalsScript = assetHash.MakeScript("decimals");
                var result = await expressNode.GetResultAsync(decimalsScript).ConfigureAwait(false);
                var decimals = (byte)(result.Stack[0].GetInteger());
                builder.EmitDynamicCall(assetHash, "transfer", senderHash, receiverHash, amount.ToBigInteger(decimals), null);
            }
            else
            {
                throw new ArgumentException($"Invalid quantity value {quantity}");
            }

            var txHash = await expressNode.ExecuteAsync(senderWallet, senderHash, WitnessScope.CalledByEntry, builder.ToArray())
                .ConfigureAwait(false);
            writer?.WriteTxHash(txHash, "Transfer");
            return txHash;
        }    
    }
}
