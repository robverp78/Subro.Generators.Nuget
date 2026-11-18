using Microsoft.CodeAnalysis;
using Viaduct.Generation;
using Viaduct.Signals;

namespace Viaduct.Tests
{
    /// <summary>What the generator makes of a [Signals] interface, and what it refuses.</summary>
    public class SignalsGeneratorTests : GeneratorTestsBase
    {
        const string Usings = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Viaduct.Signals;

            namespace Shop;

            public sealed record Summary(int Count) : ISignalMergeable<Summary>
            {
                public Summary Merge(Summary newer) => newer with { Count = Count + newer.Count };
            }

            """;

        static (IReadOnlyList<Diagnostic> Diagnostics, string Generated) Run(string contract)
        {
            Type[] _ = [typeof(SignalEnvelope), typeof(Subro.Json)];

            var compiler = createCompiler();
            compiler.CheckErrors = false;
            compiler.IncrementalGenerators.Add(new SignalsGenerator());
            var result = compiler.Create(Usings + contract);

            var generated = string.Join("\n", result.GeneratorResults
                .SelectMany(static r => r.Results)
                .SelectMany(static r => r.GeneratedSources)
                .Select(static s => s.SourceText.ToString()));
            return (result.Diagnostics, generated);
        }

        static string Code(ViaductErrors error) => error.GetCode();

        static IEnumerable<string> Errors(IReadOnlyList<Diagnostic> diagnostics)
            => diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).Select(static d => d.Id).Distinct();

        [Test]
        public async Task AValidContract_GeneratesCodeThatCompiles()
        {
            var (diagnostics, generated) = Run("""
                [Signals("shop.orders")]
                public interface IOrderSignals
                {
                    [SignalTopic("shop/{shopId}/orders")]
                    ValueTask OrdersChangedAsync(Guid shopId, Summary summary, CancellationToken cancellationToken = default);

                    [SignalTopic("shop/{shopId}/till/{till}"), Ephemeral]
                    ValueTask TillOpenedAsync(Guid shopId, int till, CancellationToken cancellationToken = default);

                    [SignalDelivery(SignalDeliveryKind.Batch, Milliseconds = 1000)]
                    ValueTask StockChangedAsync(CancellationToken cancellationToken = default);
                }
                """);

            await Assert.That(Errors(diagnostics)).IsEmpty();
            await Assert.That(generated).Contains("public sealed class OrderSignalsContract");
            await Assert.That(generated).Contains("public readonly struct OrderSignalsSubscriptions");
            await Assert.That(generated).Contains("OnOrdersChanged(global::System.Guid shopId,");
            await Assert.That(generated).Contains("\"shop.orders.StockChanged\"");
            await Assert.That(generated).Contains("h.Merge(n)");
        }

        [Test]
        public async Task AnInternalContract_GetsInternalCode()
        {
            var (diagnostics, generated) = Run("""
                [Signals("internal")]
                internal interface IQuietSignals
                {
                    ValueTask PingedAsync(CancellationToken cancellationToken = default);
                }
                """);

            await Assert.That(Errors(diagnostics)).IsEmpty();
            await Assert.That(generated).Contains("internal sealed class QuietSignalsContract");
        }

        [Test]
        [Arguments("""ValueTask GoneAsync(CancellationToken cancellationToken = default);""", "[SignalTopic(\"x/{id}\")]", ViaductErrors.SignalTopicHoleNotFilled)]
        [Arguments("""ValueTask GoneAsync(int id, CancellationToken cancellationToken = default);""", "", ViaductErrors.SignalParameterNotInTopic)]
        [Arguments("""ValueTask GoneAsync(Summary a, Summary b, CancellationToken cancellationToken = default);""", "", ViaductErrors.SignalMoreThanOnePayload)]
        [Arguments("""Task GoneAsync(CancellationToken cancellationToken = default);""", "", ViaductErrors.SignalMethodNotValueTask)]
        [Arguments("""ValueTask GoneAsync(CancellationToken cancellationToken, Summary summary);""", "", ViaductErrors.SignalCancellationTokenNotLast)]
        [Arguments("""ValueTask GoneAsync(DateTime id, CancellationToken cancellationToken = default);""", "[SignalTopic(\"x/{id}\")]", ViaductErrors.SignalHoleTypeNotSupported)]
        [Arguments("""ValueTask GoneAsync(int id, CancellationToken cancellationToken = default);""", "[SignalTopic(\"x/{id}{id2}\")]", ViaductErrors.SignalTopicInvalid)]
        [Arguments("""ValueTask GoneAsync(ref int id);""", "", ViaductErrors.SignalMethodNotSupported)]
        [Arguments("""ValueTask GoneAsync(CancellationToken cancellationToken = default);""", "[SignalDelivery(SignalDeliveryKind.Debounce)]", ViaductErrors.SignalDeliveryNeedsTime)]
        public async Task AnInvalidMethod_IsAnError(string method, string attributes, ViaductErrors expected)
        {
            var (diagnostics, generated) = Run($$"""
                [Signals("bad")]
                public interface IBadSignals
                {
                    {{attributes}}
                    {{method}}
                }
                """);

            await Assert.That(Errors(diagnostics)).Contains(Code(expected));
            await Assert.That(generated).DoesNotContain("BadSignalsContract");
        }

        [Test]
        public async Task TwoMethodsWithOneKey_AreAnError()
        {
            var (diagnostics, _) = Run("""
                [Signals("bad")]
                public interface IBadSignals
                {
                    ValueTask Changed(CancellationToken cancellationToken = default);
                    ValueTask ChangedAsync(CancellationToken cancellationToken = default);
                }
                """);

            await Assert.That(Errors(diagnostics)).Contains(Code(ViaductErrors.SignalDuplicateKey));
        }

        [Test]
        public async Task AContractThatIsNotAPlainInterface_IsAnError()
        {
            var (extending, _) = Run("""
                public interface IBase { }

                [Signals("bad")]
                public interface IBadSignals : IBase
                {
                    ValueTask ChangedAsync(CancellationToken cancellationToken = default);
                }
                """);
            var (property, _) = Run("""
                [Signals("bad")]
                public interface IBadSignals
                {
                    int Count { get; }
                }
                """);
            var (prefix, _) = Run("""
                [Signals("not a prefix")]
                public interface IBadSignals
                {
                    ValueTask ChangedAsync(CancellationToken cancellationToken = default);
                }
                """);

            await Assert.That(Errors(extending)).Contains(Code(ViaductErrors.SignalsNotAPlainInterface));
            await Assert.That(Errors(property)).Contains(Code(ViaductErrors.SignalsMemberNotAMethod));
            await Assert.That(Errors(prefix)).Contains(Code(ViaductErrors.SignalsInvalidPrefix));
        }

        [Test]
        public async Task ASignalWithoutACancellationToken_IsAWarning()
        {
            var (diagnostics, generated) = Run("""
                [Signals("loose")]
                public interface ILooseSignals
                {
                    ValueTask ChangedAsync();
                }
                """);

            await Assert.That(Errors(diagnostics)).IsEmpty();
            await Assert.That(diagnostics.Any(static d => d.Id == Code(ViaductErrors.SignalWithoutCancellationToken) && d.Severity == DiagnosticSeverity.Warning)).IsTrue();
            await Assert.That(generated).Contains("LooseSignalsContract");
        }
    }
}
