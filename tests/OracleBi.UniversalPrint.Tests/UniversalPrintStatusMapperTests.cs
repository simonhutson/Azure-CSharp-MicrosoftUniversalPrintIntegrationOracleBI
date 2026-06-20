using OracleBi.UniversalPrint.Models;
using OracleBi.UniversalPrint.UniversalPrintIntegration;
using Xunit;

namespace OracleBi.UniversalPrint.Tests;

public sealed class UniversalPrintStatusMapperTests
{
    [Fact]
    public void CompletedSuccessfully_detail_maps_to_Completed()
    {
        var state = UniversalPrintStatusMapper.MapState("processing", new[] { "completedSuccessfully" });
        Assert.Equal(PrintJobState.Completed, state);
    }

    [Theory]
    [InlineData("aborted")]
    [InlineData("documentError")]
    [InlineData("interrupted")]
    public void Failure_details_map_to_Failed(string detail)
    {
        var state = UniversalPrintStatusMapper.MapState("processing", new[] { detail });
        Assert.Equal(PrintJobState.Failed, state);
    }

    [Fact]
    public void Completed_processing_state_maps_to_Completed()
    {
        var state = UniversalPrintStatusMapper.MapState("completed", Array.Empty<string>());
        Assert.Equal(PrintJobState.Completed, state);
    }

    [Theory]
    [InlineData("aborted")]
    [InlineData("canceled")]
    [InlineData("cancelled")]
    public void Terminal_failure_processing_states_map_to_Failed(string processingState)
    {
        var state = UniversalPrintStatusMapper.MapState(processingState, Array.Empty<string>());
        Assert.Equal(PrintJobState.Failed, state);
    }

    [Theory]
    [InlineData("processing")]
    [InlineData("pending")]
    [InlineData("paused")]
    public void In_progress_processing_states_map_to_Printing(string processingState)
    {
        var state = UniversalPrintStatusMapper.MapState(processingState, Array.Empty<string>());
        Assert.Equal(PrintJobState.Printing, state);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("somethingNew")]
    public void Unknown_or_null_processing_state_defaults_to_Printing(string? processingState)
    {
        var state = UniversalPrintStatusMapper.MapState(processingState, Array.Empty<string>());
        Assert.Equal(PrintJobState.Printing, state);
    }

    [Fact]
    public void Details_take_precedence_over_processing_state()
    {
        // "completed" processing state but a failure detail -> Failed wins (details are authoritative).
        var state = UniversalPrintStatusMapper.MapState("completed", new[] { "aborted" });
        Assert.Equal(PrintJobState.Failed, state);
    }
}
