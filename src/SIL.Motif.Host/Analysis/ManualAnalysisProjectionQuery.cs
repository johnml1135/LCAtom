using SIL.Motif.Contract.Responses;
using System;
using SIL.LCModel;
using SIL.Motif.Projection;

namespace SIL.Motif.Host.Analysis;

/// <summary>Reads and shapes the manual side of the project analysis aggregate without invoking PanGloss.</summary>
public static class ManualAnalysisProjectionQuery
{
    public static AnalysisAggregateProjection Read(LcmCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return Build(AnalysisAggregateReader.Read(cache));
    }

    public static AnalysisAggregateProjection Build(AnalysisAggregateResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.HasAssessment)
        {
            throw new ArgumentException(
                "A manual analysis projection cannot discard a recorded Assessment.", nameof(response));
        }

        return AnalysisAggregateProjectionQuery.Build(response, string.Empty, string.Empty);
    }
}
