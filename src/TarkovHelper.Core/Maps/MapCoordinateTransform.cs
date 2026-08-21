namespace TarkovHelper.Core.Maps;

// World-space (X, Z) to a raw SVG image's own pixel (X, Y) space.
//
// This formula went through three rounds of bugs before landing right, and
// each round LOOKED correct in isolation (self-consistent math, matching
// maps.json's own hand-placed labels) while actually being wrong. The bug
// was only caught by cross-referencing two real screenshots from a live
// game session, whose filenames encode ground-truth world coordinates,
// against the player's own visual confirmation of where they were standing
// on the actual tarkov.dev map (top-right of Customs; top of Woods near
// Military Camp). Landmark *labels* in maps.json are not reliable ground
// truth on their own - they're hand-placed for display and can be self-
// consistent with a broken transform without revealing the bug.
//
// The pipeline, exactly as Leaflet computes it (verified by executing the
// real `leaflet` npm package under Node/jsdom, not hand-derived):
//
// Stage 1 - build a LatLngBounds from the map's raw `bounds` array using
// bounds[i][0] as lng (world X) and bounds[i][1] as lat (world Z) - verified
// against tarkov-dev's getBounds(), which swaps each pair's elements when
// constructing L.latLngBounds. LatLngBounds normalizes by plain min/max on
// these raw (un-rotated) lat/lng values to get NorthWest = (maxLat, minLng)
// and SouthEast = (minLat, maxLng).
//
// Stage 2 - project NorthWest and SouthEast through the CRS (rotate, then
// apply the [a,b,c,d] transform coefficients) to get their "transformed
// space" points.
//
// Stage 3 - Leaflet's ImageOverlay positions the SVG using
// `new L.Bounds(latLngToLayerPoint(NW), latLngToLayerPoint(SE))`. Critically,
// L.Bounds.extend() takes independent Math.min/Math.max on x and y - it does
// NOT treat the first point passed in as "top-left" by construction. For
// every real map checked (Woods, Customs), the projected NorthWest point's Y
// ends up numerically LARGER than the projected SouthEast point's Y, so
// naively mapping NW->(0,0) and SE->(width,height) inverts the image
// vertically. The fix: take the actual per-axis min/max of the two
// projected corners, exactly as L.Bounds.extend does, and use THAT as the
// remap rectangle - not raw NW/SE order.
public static class MapCoordinateTransform
{
    // Stage 2 alone: world (x,z) -> "transformed space" point (rotate then
    // apply transform coefficients). Exposed separately since stage 3 needs
    // to run this on the NorthWest/SouthEast points too.
    public static (float X, float Y) ProjectToTransformedSpace(float worldX, float worldZ, MapCalibration calibration)
    {
        if (calibration.Transform is not { Length: 4 } transform)
        {
            throw new InvalidOperationException(
                $"Map '{calibration.NormalizedName}' has no transform data - cannot place a marker.");
        }

        var (rx, ry) = ApplyRotation(worldX, worldZ, calibration.CoordinateRotationDegrees);

        var scaleX = transform[0];
        var marginX = transform[1];
        var scaleY = -transform[2];
        var marginY = transform[3];

        return (scaleX * rx + marginX, scaleY * ry + marginY);
    }

    // Full pipeline: world (x,z) -> raw SVG pixel (x,y), given the SVG's
    // actual viewBox width/height (read from the downloaded SVG's root
    // element, not part of maps.json).
    public static (float PixelX, float PixelY) WorldToSvgPixel(
        float worldX, float worldZ, MapCalibration calibration, float svgViewBoxWidth, float svgViewBoxHeight)
    {
        if (calibration.Bounds is not { Length: 2 } bounds)
        {
            throw new InvalidOperationException(
                $"Map '{calibration.NormalizedName}' has no bounds data - cannot fit marker onto SVG.");
        }

        // Stage 1: normalize by raw (un-rotated) lat/lng min/max.
        // tarkov-dev's getBounds() calls L.latLngBounds([b[0][1],b[0][0]], [b[1][1],b[1][0]])
        // - it SWAPS each pair's elements, so bounds[i][0] becomes lng and
        // bounds[i][1] becomes lat (verified by executing the real code;
        // an earlier attempt assumed bounds[i]=[lat,lng] directly and was wrong).
        var lng0 = bounds[0][0];
        var lat0 = bounds[0][1];
        var lng1 = bounds[1][0];
        var lat1 = bounds[1][1];

        var northLat = Math.Max(lat0, lat1);
        var southLat = Math.Min(lat0, lat1);
        var westLng = Math.Min(lng0, lng1);
        var eastLng = Math.Max(lng0, lng1);

        // NorthWest = (north, west) = (maxLat, minLng); SouthEast = (south, east) = (minLat, maxLng).
        // As world (x, z): x=lng, z=lat.
        var (nwX, nwY) = ProjectToTransformedSpace(westLng, northLat, calibration);
        var (seX, seY) = ProjectToTransformedSpace(eastLng, southLat, calibration);

        // L.Bounds.extend takes independent min/max per axis - replicate
        // that instead of assuming (nwX,nwY) is the top-left pixel origin.
        var minX = Math.Min(nwX, seX);
        var maxX = Math.Max(nwX, seX);
        var minY = Math.Min(nwY, seY);
        var maxY = Math.Max(nwY, seY);

        var (tx, ty) = ProjectToTransformedSpace(worldX, worldZ, calibration);

        var fracX = (tx - minX) / (maxX - minX);
        var fracY = (ty - minY) / (maxY - minY);

        return (fracX * svgViewBoxWidth, fracY * svgViewBoxHeight);
    }

    private static (float RotatedX, float RotatedY) ApplyRotation(float x, float y, float rotationDegrees)
    {
        if (rotationDegrees == 0)
        {
            return (x, y);
        }

        var angleRadians = rotationDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(angleRadians);
        var sin = MathF.Sin(angleRadians);

        var rotatedX = x * cos - y * sin;
        var rotatedY = x * sin + y * cos;

        return (rotatedX, rotatedY);
    }

    // Selects which layer/floor a given world Y (altitude) falls within,
    // or null if it's within the base/ground layer's own heightRange (or no
    // layer data exists, e.g. single-floor maps).
    public static MapLayer? SelectLayer(float worldY, MapCalibration calibration)
    {
        foreach (var layer in calibration.Layers)
        {
            foreach (var extent in layer.Extents)
            {
                if (worldY >= extent.MinHeight && worldY <= extent.MaxHeight)
                {
                    return layer;
                }
            }
        }

        return null;
    }
}
