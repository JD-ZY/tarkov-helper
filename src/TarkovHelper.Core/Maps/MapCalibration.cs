namespace TarkovHelper.Core.Maps;

public class MapHeightExtent
{
    public float MinHeight { get; set; }
    public float MaxHeight { get; set; }
}

public class MapLayer
{
    public string Name { get; set; } = string.Empty;
    public string? SvgLayer { get; set; }
    public List<MapHeightExtent> Extents { get; set; } = new();
}

// A named point of interest (e.g. "Dorms", "Old Gas") hand-placed by
// tarkov-dev's map authors for display purposes - not from live game data,
// and its world-space position is only approximate (a label's general
// area), unlike MapExtract positions which come from real extract zones.
public class MapPoi
{
    public string Text { get; set; } = string.Empty;
    public float X { get; set; }
    public float Z { get; set; }
}

// Calibration data for one map's "interactive" projection, sourced from
// tarkov-dev's maps.json (the tarkov.dev GraphQL API exposes no image or
// transform data at all - the Map.svg field is commented out in their
// schema). Fields and transform semantics verified against
// the-hideout/tarkov-dev src/pages/map/index.jsx.
public class MapCalibration
{
    public string NormalizedName { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;

    // [scaleX, marginX, scaleY, marginY] - see MapCoordinateTransform.
    public float[]? Transform { get; set; }

    public float CoordinateRotationDegrees { get; set; }
    public string? SvgPath { get; set; }
    public MapHeightExtent? HeightRange { get; set; }
    public List<MapLayer> Layers { get; set; } = new();

    // World-space [[z0,x0],[z1,x1]] corners the SVG image is stretched to
    // fill (maps.json's "bounds", or "svgBounds" if present) - needed to
    // fit the transformed marker-coordinate space onto the SVG's own raw
    // pixel/viewBox space, which is a separate, generally anisotropic scale
    // from the marker transform itself. See MapCoordinateTransform remarks.
    public float[][]? Bounds { get; set; }

    public List<MapPoi> Pois { get; set; } = new();
}
