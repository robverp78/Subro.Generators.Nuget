using System.Globalization;
using System.Text;
using Viaduct.Client;

namespace Viaduct.Tests
{
    /// <summary>
    /// How a generated client writes a query value (<see cref="ViaductClientFunctions"/>): as the server reads it, with the
    /// invariant culture, and escaped as <see cref="Uri.EscapeDataString(string)"/> would, whatever culture the caller runs in.
    /// </summary>
    public class QueryValueTests
    {
        [Flags]
        public enum Days { None = 0, Monday = 1, Tuesday = 2 }

        /// <summary>Runs <paramref name="write"/> under a culture that writes dates, numbers and the minus sign differently.</summary>
        static string Written(Func<StringBuilder, StringBuilder> write, string culture = "nb-NO")
        {
            var before = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            try
            {
                return write(new StringBuilder()).ToString();
            }
            finally
            {
                CultureInfo.CurrentCulture = before;
            }
        }

        [Test]
        public async Task Dates_AreIso_InAnyCulture()
        {
            await Assert.That(Written(sb => sb.AppendQueryValue(new DateOnly(2026, 11, 3)))).IsEqualTo("2026-11-03");
            await Assert.That(Written(sb => sb.AppendQueryValue(new TimeOnly(9, 5, 0)))).IsEqualTo("09%3A05%3A00");
            await Assert.That(Written(sb => sb.AppendQueryValue(new DateTime(2026, 11, 3, 10, 30, 0, DateTimeKind.Utc))))
                .IsEqualTo("2026-11-03T10%3A30%3A00Z");
            await Assert.That(Written(sb => sb.AppendQueryValue(new DateTimeOffset(2026, 11, 3, 10, 30, 0, TimeSpan.FromHours(1)))))
                .IsEqualTo("2026-11-03T10%3A30%3A00%2B01%3A00");
            await Assert.That(Written(sb => sb.AppendQueryValue(new TimeSpan(1, 2, 0, 0)))).IsEqualTo("1.02%3A00%3A00");
        }

        /// <summary>Norwegian writes −5 with U+2212 and 1,5 with a comma: the server reads neither.</summary>
        [Test]
        public async Task Numbers_AreInvariant()
        {
            await Assert.That(Written(sb => sb.AppendQueryValue(-5))).IsEqualTo("-5");
            await Assert.That(Written(sb => sb.AppendQueryValue(1.5m))).IsEqualTo("1.5");
            await Assert.That(Written(sb => sb.AppendQueryValue(1e20))).IsEqualTo("1E%2B20");
        }

        [Test]
        public async Task OtherValueTypes_AreWrittenAsTheServerParsesThem()
        {
            var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

            await Assert.That(Written(sb => sb.AppendQueryValue(id))).IsEqualTo("11111111-2222-3333-4444-555555555555");
            await Assert.That(Written(sb => sb.AppendQueryValue(true))).IsEqualTo("true");
            await Assert.That(Written(sb => sb.AppendQueryValue(Days.Monday | Days.Tuesday))).IsEqualTo("Monday%2C%20Tuesday");
        }

        [Test]
        [Arguments("plain")]
        [Arguments("a&b c+d=e/f?g#h")]
        [Arguments("æøå Ünïcödé")]
        [Arguments("emoji 🏸 and ~-._")]
        [Arguments("")]
        public async Task Text_IsEscapedAsEscapeDataStringDoes(string text)
        {
            await Assert.That(Written(sb => sb.AppendQueryValue(text))).IsEqualTo(Uri.EscapeDataString(text));
        }

        /// <summary>A surrogate without its pair cannot be UTF-8: written as U+FFFD, rather than throwing mid-call.</summary>
        [Test]
        public async Task AnUnpairedSurrogate_IsWrittenAsTheReplacementCharacter()
        {
            await Assert.That(Written(sb => sb.AppendQueryValue("a\uD83Cb"))).IsEqualTo("a%EF%BF%BDb");
        }

        [Test]
        public async Task AnyOtherValue_IsWrittenThroughObject_AndNullIsNothing()
        {
            await Assert.That(Written(sb => sb.AppendQueryValue((object)new DateOnly(2026, 11, 3)))).IsEqualTo("11%2F03%2F2026");
            await Assert.That(Written(sb => sb.AppendQueryValue((object?)null))).IsEqualTo("");
            await Assert.That(Written(sb => sb.AppendQueryValue((object)new Uri("https://example.test/a b")))).IsEqualTo("https%3A%2F%2Fexample.test%2Fa%20b");
        }
    }
}
