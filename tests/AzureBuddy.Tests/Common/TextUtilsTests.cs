using AzureBuddy.Core.Common;
using Xunit;

namespace AzureBuddy.Tests.Common;

public class TextUtilsTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("12172", "12172")]
    [InlineData("#12172", "12172")]
    [InlineData("12172.0", "121720")]
    [InlineData("12172,12171", "1217212171")]
    [InlineData("no digits here", "")]
    [InlineData("id: 42, and 7", "427")]
    public void DigitsOnly_StripsEverythingButDigits(string? input, string expected)
    {
        Assert.Equal(expected, TextUtils.DigitsOnly(input));
    }
}
