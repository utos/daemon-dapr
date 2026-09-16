using System.Text.Json;
using Common.Workflow.Serialization;

namespace UnitTests.Serialization;

/// <summary>
/// The one place the daemon decides what CLR type a number is. Scriban dispatches arithmetic and
/// comparisons on the runtime type, so these are behavioural assertions, not housekeeping — four
/// separate copies of this rule existed and they did not all agree.
/// </summary>
public class NumberNarrowingTests
{
    public class FromDouble
    {
        [Theory]
        [InlineData(7d, typeof(int))]
        [InlineData(-7d, typeof(int))]
        [InlineData(0d, typeof(int))]
        [InlineData(5_000_000_000d, typeof(long))]
        [InlineData(2.5d, typeof(double))]
        [InlineData(1e19, typeof(double))]     // beyond long
        [InlineData(double.NaN, typeof(double))]
        [InlineData(double.PositiveInfinity, typeof(double))]
        [InlineData(double.NegativeInfinity, typeof(double))]
        public void Narrows_by_value(double value, Type expected)
        {
            Assert.IsType(expected, NumberNarrowing.FromDouble(value));
        }

        [Fact]
        public void Int_max_stays_an_int_and_one_past_it_becomes_a_long()
        {
            // int.MaxValue is exactly representable as a double, so this boundary is exact.
            Assert.Equal(int.MaxValue, Assert.IsType<int>(NumberNarrowing.FromDouble(int.MaxValue)));
            Assert.Equal(2147483648L, Assert.IsType<long>(NumberNarrowing.FromDouble(2147483648d)));
        }

        [Fact]
        public void Two_pow_63_stays_a_double_rather_than_saturating_to_long_max()
        {
            // long.MaxValue is NOT representable as a double — it rounds up to exactly 2^63. A
            // naive "value <= long.MaxValue" therefore admits 2^63, and the cast saturates back
            // down to long.MaxValue, silently changing the number to one less than it was.
            const double twoPow63 = 9223372036854775808d;

            Assert.IsType<double>(NumberNarrowing.FromDouble(twoPow63));
            Assert.IsType<double>(NumberNarrowing.FromDouble(-twoPow63 * 2));
        }

        [Fact]
        public void Negative_zero_loses_its_sign()
        {
            // Pinned because it is a real, deliberate loss: -0.0 is whole and in int range, so it
            // narrows to int 0. Nothing downstream distinguishes the two.
            Assert.Equal(0, Assert.IsType<int>(NumberNarrowing.FromDouble(-0.0)));
        }
    }

    public class Read
    {
        [Theory]
        [InlineData("7", typeof(int))]
        [InlineData("2.5", typeof(double))]
        [InlineData("5000000000", typeof(long))]
        [InlineData("1e19", typeof(double))]
        public void Narrows_by_token(string json, Type expected)
        {
            Assert.IsType(expected, ReadNumber(json));
        }

        [Fact]
        public void A_whole_number_written_as_a_decimal_stays_a_double()
        {
            // The one case where the two rules deliberately differ. JSON still has the author's
            // token and can tell 5.0 from 5; proto carries only a double and cannot. Asserted so
            // the difference is a decision on the record rather than an accident.
            Assert.IsType<double>(ReadNumber("5.0"));
            Assert.IsType<int>(NumberNarrowing.FromDouble(5.0));
        }

        [Fact]
        public void An_integer_beyond_double_precision_is_not_rounded()
        {
            // 2^53 + 1. Read through a double this would come back as ...992. The integer
            // attempts come first precisely so it does not.
            Assert.Equal(9007199254740993L, Assert.IsType<long>(ReadNumber("9007199254740993")));
        }

        private static object ReadNumber(string json)
        {
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
            reader.Read();
            return NumberNarrowing.Read(ref reader);
        }
    }
}
