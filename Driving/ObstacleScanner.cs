using System;
using System.Collections.Generic;
using System.Numerics;
using ETS2LA.Game.SDK;

namespace AutoParking;

/// <summary>
///  Fills an <see cref="ObstacleSnapshot"/> from the game's live vehicle lists.
/// </summary>
public static class ObstacleScanner
{
    public static ObstacleSnapshot Scan(Vector2 center, double radiusM)
    {
        ObstacleSnapshot snapshot = new();
        double squaredRadius = radiusM * radiusM;

        try
        {
            TrafficVehicle[]? traffic = TrafficProvider.Current.GetCurrentTrafficData()?.vehicles;
            if (traffic != null)
            {
                for (int i = 0; i < traffic.Length; i++)
                {
                    AddIfNear(snapshot, traffic[i], center, squaredRadius);
                }
            }
        }
        catch
        {
            // Traffic provider not reading (SDK offline) - parking just loses obstacle data.
        }

        try
        {
            List<ParkedVehicle>? parked = ParkedVehiclesProvider.Current.GetCurrentParkedVehicleData()?.vehicles;
            if (parked != null)
            {
                for (int i = 0; i < parked.Count; i++)
                {
                    AddIfNear(snapshot, parked[i], center, squaredRadius);
                }
            }
        }
        catch
        {
        }

        snapshot.BuiltUtc = DateTime.UtcNow;
        return snapshot;
    }

    private static void AddIfNear(ObstacleSnapshot snapshot, BaseVehicle? vehicle, Vector2 center, double squaredRadius)
    {
        if (vehicle == null)
            return;

        Vector2 position = Geometry.ToPlane(vehicle.Position);
        double dx = position.X - center.X;
        double dy = position.Y - center.Y;
        if (dx * dx + dy * dy > squaredRadius)
            return;

        List<Vector3>? corners;
        try
        {
            corners = vehicle.GetCornersOnGround();
        }
        catch
        {
            return;
        }

        if (corners == null || corners.Count < 3)
            return;

        Vector2[] polygon = new Vector2[corners.Count];
        for (int i = 0; i < corners.Count; i++)
        {
            polygon[i] = Geometry.ToPlane(corners[i]);
        }

        snapshot.Polygons.Add(polygon);
    }
}
