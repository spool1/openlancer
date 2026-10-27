using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using LibreLancer.ImUI.ImPlot;
using LibreLancer.Thn;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The curve editor of an event (section 7.5 of the M8 design): a graph of the curve over the length of the event, as
/// <c>ParameterCurve.GetValue</c> gives it (ParameterCurve.cs:106), with a handle for each point, and a table of the
/// points. A drag of a handle is one entry of the edit history (see <see cref="CurvePointDrag"/>), and each field of
/// the table is one edit.
/// </summary>
internal sealed class ThnCurveEditor(EditorWindow window, PropertyGrid grid)
{
    // Sizes in unscaled points.
    private const float PlotHeight = 140;
    private const float HandleSize = 5;

    /// <summary>The samples of the line of the graph.</summary>
    private const int Samples = 128;

    private static readonly PropertyGrid.ComponentLabel[] PointLabels =
    [
        new("Time", EditorTheme.AxisX), new("Value", EditorTheme.AxisY), new("In", EditorTheme.TextDim),
        new("Out", EditorTheme.TextDim),
    ];

    private CurvePointDrag? drag;
    private (ThnRef Event, int Index)? dragged;

    /// <summary>True while a drag of a point is open.</summary>
    public bool IsDragging => drag != null;

    /// <summary>Ends an open drag when the left button is up, also when the page does not draw the curve. Call it once
    /// in each frame.</summary>
    public void Frame()
    {
        if (drag != null && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            End();
        }
    }

    /// <summary>Ends an open drag with the point that the edit view holds.</summary>
    public void End()
    {
        drag?.End();
        drag = null;
        dragged = null;
    }

    /// <summary>Draws the section of the curve of an event: the graph, the type, the period and the points.</summary>
    /// <param name="length">The length of the event, in seconds.</param>
    public void Draw(ThnRef ev, ThnTable curve, float? periodMilliseconds, float length, ThnFieldState field)
    {
        if (!grid.BeginSection("Thn.Event.Curve", EditorIcons.Stream, "Curve"))
        {
            return;
        }

        var points = ThnCurve.Points(curve);
        var type = (curve[ThnCurve.TypeKey] as ThnText)?.Value ?? "";
        var typeField = field with
        {
            Path = new ThnPath($"{ThnCurve.Path}.{ThnCurve.TypeKey}"), Kind = ThnKeyKind.Text,
            Value = curve[ThnCurve.TypeKey], Evidence = "ParameterCurve.cs:44-80",
        };
        grid.ThnTextRow("Type", typeField, "FreeFormPCurve and CatmullRomPCurve are the types that retail uses.",
            text => ThnFieldParse.Of(new ThnText(text)), "Not set");
        if (points == null)
        {
            grid.Text("Points", "The curve has no list of points with four numbers each. Edit it as Lua text in Other keys.");
            grid.EndSection();
            return;
        }

        // A period above 0 repeats the curve. Else the curve spans the length of the event (ParameterCurve.cs:106-110).
        var span = periodMilliseconds is { } period && period / 1000 >= float.Epsilon ? period / 1000 : length;
        grid.Row("Graph", () => Plot(ev, points, Engine(type, points, periodMilliseconds), span, length, field.ReadOnly));
        for (var i = 0; i < points.Count; i++)
        {
            var index = i;
            var point = points[i];
            var pointField = field with
            {
                Kind = ThnKeyKind.Quaternion,
                Value = ThnTable.FromList(new ThnNumber(point.Time), new ThnNumber(point.Value), new ThnNumber(point.In),
                    new ThnNumber(point.Out)),
                Line = i,
            };
            grid.ThnComponentRow($"Point {i + 1}", pointField, null, PointLabels, text => PointParse(curve, index, text));
        }

        grid.EndSection();
    }

    /// <summary>The graph and its handles. A drag of a handle opens a <see cref="CurvePointDrag"/>.</summary>
    private void Plot(ThnRef ev, IReadOnlyList<ThnCurvePoint> points, ParameterCurve curve, float span, float length,
        string? readOnly)
    {
        var xs = new float[Samples];
        var ys = new float[Samples];
        for (var i = 0; i < Samples; i++)
        {
            // The last sample is just before the end of a period, which gives the start of the next period.
            xs[i] = span * i / (Samples - 1) * 0.9999f;
            ys[i] = curve.GetValue(xs[i], Math.Max(length, 1e-6f));
        }

        var low = Math.Min(ys.Min(), points.Min(p => p.Value));
        var high = Math.Max(ys.Max(), points.Max(p => p.Value));
        var pad = Math.Max((high - low) * 0.1f, 0.05f);
        if (!ImPlot.BeginPlot("##curve", new Vector2(-1, PlotHeight * ImGuiHelper.Scale),
                ImPlotFlags.NoLegend | ImPlotFlags.NoMenus | ImPlotFlags.NoBoxSelect | ImPlotFlags.NoTitle))
        {
            return;
        }

        ImPlot.SetupAxes(null, null);
        ImPlot.SetupAxisLimits(ImAxis.X1, 0, span, ImPlotCond.Always);
        ImPlot.SetupAxisLimits(ImAxis.Y1, low - pad, high + pad, ImPlotCond.Always);
        ImPlot.PlotLine("##value", ref xs[0], ref ys[0], Samples);
        for (var i = 0; i < points.Count; i++)
        {
            var shown = drag != null && dragged == (ev, i) ? drag.Applied : points[i];
            double x = shown.Time * span, y = shown.Value;
            var moved = ImPlot.DragPoint(i, ref x, ref y, EditorTheme.Accent, HandleSize * ImGuiHelper.Scale,
                readOnly != null ? ImPlotDragToolFlags.NoInputs : ImPlotDragToolFlags.None, out _, out _, out var held);
            if (held && drag == null && readOnly == null && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                Begin(ev, i);
            }

            if (moved && drag != null && dragged == (ev, i) && span > 0)
            {
                Move((float)(x / span), (float)y);
            }
        }

        ImPlot.EndPlot();
        ImGui.SetItemTooltip(readOnly ?? "Drag a point to change the curve. The table below edits the exact values.");
    }

    private void Begin(ThnRef ev, int index)
    {
        if (window.Session is not { } session)
        {
            return;
        }

        try
        {
            drag = CurvePointDrag.Begin(session.Edits, ev, index);
            dragged = (ev, index);
        }
        catch (InvalidOperationException e)
        {
            window.Log.Add(ConsoleSeverity.Warning, EditController.Source, $"The curve point did not move: {ErrorText.Of(e)}");
        }
    }

    private void Move(float time, float value)
    {
        try
        {
            drag!.Move(time, value);
        }
        catch (InvalidOperationException e)
        {
            window.Log.Add(ConsoleSeverity.Error, EditController.Source, $"The drag of the curve point stopped: {ErrorText.Of(e)}");
            drag!.Cancel();
            drag = null;
            dragged = null;
        }
    }

    /// <summary>The engine curve of the points, to draw the line: the type of the CLSID (ParameterCurve.cs:47-80) and
    /// the period.</summary>
    private static ParameterCurve Engine(string type, IReadOnlyList<ThnCurvePoint> points, float? period)
    {
        var name = type.EndsWith("PCurve", StringComparison.OrdinalIgnoreCase) ? type[..^"PCurve".Length] : type;
        var kind = Enum.TryParse<PCurveType>(name, true, out var parsed) ? parsed : PCurveType.Unknown;
        return new ParameterCurve(kind, points.Select(p => new Vector4(p.Time, p.Value, p.In, p.Out)))
        {
            Period = period ?? -1,
        };
    }

    /// <summary>The curve that the text of a row of a point gives: the time, the value and the two slopes of the
    /// point.</summary>
    internal static ThnFieldParse PointParse(ThnTable curve, int index, string text)
    {
        var parse = ThnFieldEdit.Parse(ThnKeyKind.Quaternion, text, Runtime.Thn.ThornEvaluator.Instance);
        if (parse.Value is not ThnTable { List: [ThnNumber t, ThnNumber v, ThnNumber i, ThnNumber o] })
        {
            return parse;
        }

        return ThnFieldParse.Of(ThnCurve.WithPoint(curve, index, new ThnCurvePoint(t.Value, v.Value, i.Value, o.Value)));
    }
}
