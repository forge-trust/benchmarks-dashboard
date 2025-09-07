// Copyright (c) Martin Costello, 2024. All rights reserved.
// Licensed under the Apache 2.0 license. See the LICENSE file in the project root for full license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using MartinCostello.Benchmarks.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MartinCostello.Benchmarks.Components;

public partial class MultiBenchmark
{
    private static readonly JsonSerializerOptions SerializationOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals | JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>
    /// Gets the suite name.
    /// </summary>
    [Parameter]
    public required string Suite { get; init; }

    /// <summary>
    /// Gets the logical base benchmark name (without job suffix).
    /// </summary>
    [Parameter]
    public required string BaseName { get; init; }

    /// <summary>
    /// Gets the series grouped by job name. Key = Job name; Value = items.
    /// </summary>
    [Parameter]
    public required Dictionary<string, IList<BenchmarkItem>> Series { get; init; }

    /// <summary>
    /// Gets the kind of multi-series chart to render: "time" or "memory".
    /// </summary>
    [Parameter]
    public required string Kind { get; init; }

    /// <summary>
    /// Gets the <see cref="IJSRuntime"/> to use.
    /// </summary>
    [Inject]
    public required IJSRuntime JS { get; init; }

    private string TitleName => Kind.Equals("memory", StringComparison.OrdinalIgnoreCase) ? $"{BaseName} — Memory" : $"{BaseName} — Time";

    private string Id => $"{Suite}-{BaseName}-{Kind}";

    private string ChartId => $"{Suite}-{BaseName}-{Kind}-chart";

    /// <inheritdoc/>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var current = Options.Value;

        // Fallback color palette for multiple job series
        string[] palette = [
            "#1f77b4", // blue
            "#ff7f0e", // orange
            "#2ca02c", // green
            "#d62728", // red
            "#9467bd", // purple
            "#8c564b", // brown
            "#e377c2", // pink
            "#7f7f7f", // gray
            "#bcbd22", // olive
            "#17becf", // cyan
        ];

        var config = new
        {
            dataset = Series,
            imageFormat = current.ImageFormat,
            kind = Kind,
            name = TitleName,
            palette,
            suiteName = Suite,
            errorBars = current.ErrorBars,
        };

        var options = System.Text.Json.JsonSerializer.Serialize(config, SerializationOptions);
        await JS.InvokeVoidAsync("renderMultiChart", [ChartId, options]);

        if (firstRender)
        {
            await JS.InvokeVoidAsync("scrollToActiveChart", []);
        }
    }

    /// <inheritdoc/>
    protected override bool ShouldRender() => true;
}
