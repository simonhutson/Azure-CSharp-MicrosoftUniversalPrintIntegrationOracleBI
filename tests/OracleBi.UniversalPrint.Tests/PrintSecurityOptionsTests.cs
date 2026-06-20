using OracleBi.UniversalPrint.Configuration;
using Xunit;

namespace OracleBi.UniversalPrint.Tests;

public sealed class PrintSecurityOptionsTests
{
    [Fact]
    public void Empty_allow_list_permits_any_path()
    {
        var options = new PrintSecurityOptions();

        Assert.True(options.AllowsAllPaths);
        Assert.True(options.IsReportPathAllowed("/Anything/Report.xdo"));
    }

    [Theory]
    [InlineData("/Finance/Invoices/Invoice.xdo", true)]
    [InlineData("Finance/Invoices/Invoice.xdo", true)]   // missing leading slash is normalised
    [InlineData("/finance/invoices/x.xdo", true)]        // case-insensitive
    [InlineData("/HR/Salaries/Report.xdo", false)]
    public void Allow_list_matches_by_prefix(string reportPath, bool expected)
    {
        var options = new PrintSecurityOptions
        {
            AllowedReportPathPrefixes = ["/Finance/"],
        };

        Assert.Equal(expected, options.IsReportPathAllowed(reportPath));
    }
}
