using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SailwindVirtualCrew
{
    /// <summary>
    /// Developer tool for tuning the packing solver against ground truth. The player places an item by hand somewhere
    /// it really fits, looks at it and presses the claim key: "this spot works". The solver then judges that spot by
    /// its own rules and reports each place it disagrees:
    ///
    ///  - shape assumptions: the solver only tries items upright, turned 0 or 90 degrees, on a 10cm grid;
    ///  - the painted area: whether it covers the item's footprint, with room under the ceiling;
    ///  - physics at the exact spot: the drop and support test at the item's own position and heading;
    ///  - the nearest candidate the solver could have tried: whether the coarse pass keeps it, and what physics says.
    ///
    /// Each claim is logged and appended to BepInEx/VirtualCrewCargoClaims.tsv, so claims collect into test cases.
    /// </summary>
    internal static partial class CargoPackingSolver
    {
        private const string ClaimsFileName = "VirtualCrewCargoClaims.tsv";

        internal static void ClaimSpot(ShipItem item)
        {
            if (!item)
                return;

            var context = CrewBoatContextResolver.Resolve();
            var area = CargoAreaPainter.GetActiveArea();
            if (context == null || area == null)
            {
                Notify("No active boat to claim a spot on.");
                return;
            }

            if (item.held)
            {
                Notify("Put the item down where it fits, then claim the spot.");
                return;
            }

            if (item.currentActualBoat != context.WorldBoat || item.currentWalkCol != context.WalkCol)
            {
                Notify("The item must be aboard this boat to claim its spot.");
                return;
            }

            var body = item.GetItemRigidbody();
            if (!body || !TryGetBodyBox(body, out Vector3 boxCenter, out Vector3 size))
            {
                Notify("Can't work out the shape of '" + item.name + "'.");
                return;
            }

            ClearPreview();
            walkCol = context.WalkCol;
            selectedBody = body;

            bool previousBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                BuildCargoColumns();
                var report = BuildClaimReport(context, area, item, body, boxCenter, size);
                WriteClaim(report);
            }
            finally
            {
                Physics.queriesHitBackfaces = previousBackfaces;
            }
        }

        private sealed class ClaimReport
        {
            public string Vessel;
            public string Item;
            public Vector3 Size;
            public Vector3 Center;
            public float Bottom;
            public float Top;
            public float Yaw;
            public float YawOffGrid;
            public float Tilt;
            public int FootprintColumns;
            public int UnpaintedColumns;
            public string PaintedSummary;
            public Outcome ExactOutcome;
            public string ExactDetail;
            public float GridOffset;
            public string GridCoarse;
            public Outcome? GridOutcome;
            public string GridDetail;
        }

        private static ClaimReport BuildClaimReport(CrewBoatContext context, CargoArea area, ShipItem item, ItemRigidbody body, Vector3 boxCenter, Vector3 size)
        {
            var report = new ClaimReport
            {
                Vessel = context.WorldBoat.name.Replace("(Clone)", "").Trim(),
                Item = item.name,
                Size = size
            };

            // The item's actual pose, from its physics body in walk-collider space.
            Quaternion rotation = Quaternion.Inverse(walkCol.rotation) * body.transform.rotation;
            Vector3 center = walkCol.InverseTransformPoint(body.transform.TransformPoint(boxCenter));
            Vector3 half = size * 0.5f;
            float bottom = float.MaxValue, top = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                float y = (center + rotation * new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z)).y;
                bottom = Mathf.Min(bottom, y);
                top = Mathf.Max(top, y);
            }

            Vector3 forward = rotation * Vector3.forward;
            float yaw = Mathf.Repeat(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, 360f);
            float gridYaw = Mathf.Round(yaw / 90f) * 90f;
            report.Center = center;
            report.Bottom = bottom;
            report.Top = top;
            report.Yaw = yaw;
            report.YawOffGrid = Mathf.DeltaAngle(gridYaw, yaw);
            report.Tilt = Vector3.Angle(rotation * Vector3.up, Vector3.up);

            // Painted coverage of the footprint (upright, at the item's own heading).
            Quaternion upright = Quaternion.Euler(0f, yaw, 0f);
            float floorRest = float.MinValue, minCeiling = float.MaxValue, minFloor = float.MaxValue;
            var missing = new List<string>();
            MeasureFootprint(area, center, upright, half, bottom, top, ref report.FootprintColumns, ref report.UnpaintedColumns,
                missing, ref floorRest, ref minCeiling, ref minFloor);
            report.PaintedSummary = report.FootprintColumns == 0
                ? "no footprint"
                : (report.FootprintColumns - report.UnpaintedColumns) + "/" + report.FootprintColumns + " columns painted"
                    + (missing.Count > 0 ? " (unpainted e.g. " + string.Join(" ", missing.ToArray()) + ")" : "")
                    + (floorRest > float.MinValue ? "; painted floor " + floorRest.ToString("0.00") + " vs item bottom " + bottom.ToString("0.00")
                        + ", painted ceiling " + minCeiling.ToString("0.00") + " vs item top " + top.ToString("0.00") : "");

            // Physics at the exact spot, independent of the painting: drop from just above where it sits.
            lastSweepHit = null;
            report.ExactOutcome = EvaluatePose(center.x, center.z, upright, size, bottom - 0.3f, top + 0.05f, bottom - 0.3f,
                out var exact, out float exactRest, out int exactSupport);
            report.ExactDetail = DescribeAttempt(exactRest, exactSupport, bottom)
                + (report.ExactOutcome == Outcome.Valid ? " contacts=" + exact.SideContacts : "");

            // The nearest candidate the solver generates: snapped to 0/90 degrees and the 10cm grid.
            int yawIndex = Mathf.Abs(Mathf.RoundToInt(gridYaw / 90f)) % 2;
            float sizeX = yawIndex == 0 ? size.x : size.z;
            float sizeZ = yawIndex == 0 ? size.z : size.x;
            int ix = Mathf.RoundToInt((center.x - sizeX * 0.5f) / VoxelSize);
            int iz = Mathf.RoundToInt((center.z - sizeZ * 0.5f) / VoxelSize);
            float gridX = ix * VoxelSize + sizeX * 0.5f;
            float gridZ = iz * VoxelSize + sizeZ * 0.5f;
            report.GridOffset = new Vector2(gridX - center.x, gridZ - center.z).magnitude;

            if (!TryFindAnchor(area, ix, iz, bottom, size.y, out var anchor))
            {
                report.GridCoarse = "anchor column (" + ix + ", " + iz + ") has no painted run at the item's height";
                return report;
            }

            int cellsX = Mathf.Max(1, Mathf.CeilToInt((sizeX - 0.001f) / VoxelSize));
            int cellsZ = Mathf.Max(1, Mathf.CeilToInt((sizeZ - 0.001f) / VoxelSize));
            lastCoarseFailure = null;
            if (!TryCoarseFit(area, anchor, cellsX, cellsZ, size.y, out float candidateFloor, out float estimatedRest, out float candidateCeiling, out float candidateMinFloor))
            {
                report.GridCoarse = "rejected: " + lastCoarseFailure;
                return report;
            }

            report.GridCoarse = "kept (floorRest=" + candidateFloor.ToString("0.00")
                + " estRest=" + (estimatedRest > candidateFloor + 50f ? "on-cargo-no-room, tried last" : estimatedRest.ToString("0.00"))
                + " ceiling=" + candidateCeiling.ToString("0.00") + ")";

            var candidate = new Candidate
            {
                Ix = ix,
                Iz = iz,
                YawIndex = yawIndex,
                FloorRest = candidateFloor,
                EstimatedRest = estimatedRest,
                MinCeiling = candidateCeiling,
                MinFloor = candidateMinFloor
            };
            lastSweepHit = null;
            report.GridOutcome = Evaluate(candidate, size, out var gridPlacement, out float gridRest, out int gridSupport);
            report.GridDetail = DescribeAttempt(gridRest, gridSupport, bottom)
                + (report.GridOutcome == Outcome.Valid ? " contacts=" + gridPlacement.SideContacts : "");
            return report;
        }

        // Columns whose centres lie inside the upright footprint; for each, the painted run best overlapping the item.
        private static void MeasureFootprint(CargoArea area, Vector3 center, Quaternion upright, Vector3 half, float bottom, float top,
            ref int columns, ref int unpainted, List<string> missing, ref float floorRest, ref float minCeiling, ref float minFloor)
        {
            Quaternion inverse = Quaternion.Inverse(upright);
            float reach = Mathf.Sqrt(half.x * half.x + half.z * half.z);
            int x0 = CargoArea.ToIndex(center.x - reach), x1 = CargoArea.ToIndex(center.x + reach);
            int z0 = CargoArea.ToIndex(center.z - reach), z1 = CargoArea.ToIndex(center.z + reach);

            for (int ix = x0; ix <= x1; ix++)
            {
                for (int iz = z0; iz <= z1; iz++)
                {
                    Vector3 inBox = inverse * new Vector3(CargoArea.CellCenter(ix) - center.x, 0f, CargoArea.CellCenter(iz) - center.z);
                    if (Mathf.Abs(inBox.x) > half.x || Mathf.Abs(inBox.z) > half.z)
                        continue;

                    columns++;
                    bool found = false;
                    float bestOverlap = 0f;
                    var chosen = default(CargoArea.Run);
                    if (area.TryGetColumn(ix, iz, out var runs))
                    {
                        foreach (var run in runs)
                        {
                            float overlap = Mathf.Min(run.CeilingY, top) - Mathf.Max(run.FloorY, bottom - 0.1f);
                            if (overlap > bestOverlap)
                            {
                                bestOverlap = overlap;
                                chosen = run;
                                found = true;
                            }
                        }
                    }

                    if (!found)
                    {
                        unpainted++;
                        if (missing.Count < 4)
                            missing.Add("(" + ix + "," + iz + ")");
                        continue;
                    }

                    floorRest = Mathf.Max(floorRest, chosen.FloorY);
                    minCeiling = Mathf.Min(minCeiling, chosen.CeilingY);
                    minFloor = Mathf.Min(minFloor, chosen.FloorY);
                }
            }
        }

        private static bool TryFindAnchor(CargoArea area, int ix, int iz, float bottom, float height, out CargoArea.PaintedRun anchor)
        {
            anchor = default(CargoArea.PaintedRun);
            if (!area.TryGetColumn(ix, iz, out var runs))
                return false;

            float bestOverlap = 0f;
            bool found = false;
            foreach (var run in runs)
            {
                float overlap = Mathf.Min(run.CeilingY, bottom + height) - Mathf.Max(run.FloorY, bottom - 0.1f);
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    anchor = new CargoArea.PaintedRun { Ix = ix, Iz = iz, Run = run };
                    found = true;
                }
            }

            return found;
        }

        private static string DescribeAttempt(float restBottom, int supportHits, float actualBottom)
        {
            return (float.IsNaN(restBottom) ? "" : "restBottom=" + restBottom.ToString("0.00")
                    + " (actual " + actualBottom.ToString("0.00") + ") ")
                + "support=" + supportHits + "/" + (SupportSamplesPerSide * SupportSamplesPerSide)
                + (lastSweepHit != null ? " hit=" + lastSweepHit : "");
        }

        private static void WriteClaim(ClaimReport r)
        {
            string gridOutcome = r.GridOutcome.HasValue ? r.GridOutcome.Value.ToString() : "not tried";
            CrewDebugLog.Ok(Phase, "CLAIM item='" + r.Item + "' vessel='" + r.Vessel + "' size=" + Format(r.Size)
                + " center=" + Format(r.Center) + " bottom=" + r.Bottom.ToString("0.00"));
            CrewDebugLog.Ok(Phase, "  pose: yaw=" + r.Yaw.ToString("0.0") + " (" + r.YawOffGrid.ToString("+0.0;-0.0") + " off 0/90)"
                + " tilt=" + r.Tilt.ToString("0.0") + (r.Tilt > 5f ? " (solver only tries upright)" : ""));
            CrewDebugLog.Ok(Phase, "  painted: " + r.PaintedSummary);
            CrewDebugLog.Ok(Phase, "  physics at this exact spot: " + r.ExactOutcome + " " + r.ExactDetail);
            CrewDebugLog.Ok(Phase, "  nearest grid candidate (" + r.GridOffset.ToString("0.00") + "m away): coarse " + r.GridCoarse);
            if (r.GridOutcome.HasValue)
                CrewDebugLog.Ok(Phase, "  nearest grid candidate physics: " + gridOutcome + " " + r.GridDetail);

            Notify("Claim logged: exact spot " + r.ExactOutcome + ", nearest candidate "
                + (r.GridOutcome.HasValue ? gridOutcome : "not generated")
                + (r.UnpaintedColumns > 0 ? ", " + r.UnpaintedColumns + " cols unpainted" : ""));

            try
            {
                string path = Path.Combine(BepInEx.Paths.BepInExRootPath, ClaimsFileName);
                bool newFile = !File.Exists(path);
                using (var writer = new StreamWriter(path, append: true))
                {
                    if (newFile)
                        writer.WriteLine(string.Join("\t", new[]
                        {
                            "time", "vessel", "item", "size", "center", "bottom", "yaw", "yawOffGrid", "tilt",
                            "footprintColumns", "unpaintedColumns", "painted", "exactOutcome", "exactDetail",
                            "gridOffset", "gridCoarse", "gridOutcome", "gridDetail"
                        }));

                    writer.WriteLine(string.Join("\t", new[]
                    {
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), r.Vessel, r.Item, Format(r.Size), Format(r.Center),
                        r.Bottom.ToString("0.000"), r.Yaw.ToString("0.0"), r.YawOffGrid.ToString("0.0"), r.Tilt.ToString("0.0"),
                        r.FootprintColumns.ToString(), r.UnpaintedColumns.ToString(), r.PaintedSummary,
                        r.ExactOutcome.ToString(), r.ExactDetail, r.GridOffset.ToString("0.00"), r.GridCoarse,
                        gridOutcome, r.GridDetail ?? ""
                    }));
                }
            }
            catch (Exception ex)
            {
                CrewDebugLog.Warn(Phase, "Could not write claims file: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
