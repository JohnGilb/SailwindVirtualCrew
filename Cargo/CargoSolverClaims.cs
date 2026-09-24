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
    ///  - shape assumptions: the solver only tries its poses (90-degree turns), on a 10cm grid;
    ///  - the painted area: whether it covers every column the item reaches, with room under the ceiling;
    ///  - physics at the exact spot: the start, drop and support test at the item's own position and orientation;
    ///  - the nearest candidate the solver could have tried: whether the coarse pass keeps it, and what physics says.
    ///
    /// Each claim is logged and appended to BepInEx/VirtualCrewCargoClaims-v3.tsv, so claims collect into test cases.
    /// </summary>
    internal static partial class CargoPackingSolver
    {
        // v3: columns changed when the solver moved to real shapes (v2) and then to 90-degree poses (v3).
        private const string ClaimsFileName = "VirtualCrewCargoClaims-v3.tsv";

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
            if (!body)
            {
                Notify("'" + item.name + "' has no physics body yet.");
                return;
            }

            // A claim uses the solver's working state, which a plan in progress (paused between frames) also holds.
            if (CargoLoadPlanner.IsBusy)
            {
                Notify("Still planning cargo; claim again in a moment.");
                return;
            }

            ClearPreview();
            walkCol = context.WalkCol;
            selectedItem = item;
            selectedBody = body;

            bool shapeBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                if (!AcquireShape(item, body))
                {
                    Notify("Can't work out the shape of '" + item.name + "'.");
                    return;
                }

                BuildCargoColumns();
                EnterPhysics();
                ClaimReport report;
                try
                {
                    report = BuildClaimReport(context, area, item, body);
                }
                finally
                {
                    LeavePhysics();
                }

                WriteClaim(report);
            }
            finally
            {
                ReleaseProbe();
                Physics.queriesHitBackfaces = shapeBackfaces;
            }
        }

        private sealed class ClaimReport
        {
            public string Vessel;
            public string Item;
            public string Shape;
            public Vector3 Origin;
            public float Bottom;
            public string NearestPose;
            public float PoseOffAngle;
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

        private static ClaimReport BuildClaimReport(CrewBoatContext context, CargoArea area, ShipItem item, ItemRigidbody body)
        {
            var report = new ClaimReport
            {
                Vessel = context.WorldBoat.name.Replace("(Clone)", "").Trim(),
                Item = item.name,
                Shape = lastShapeSummary
            };

            // The item's actual pose, from its physics body in walk-collider space.
            Quaternion rotation = Quaternion.Inverse(walkCol.rotation) * body.transform.rotation;
            Vector3 origin = walkCol.InverseTransformPoint(body.transform.position);

            report.Origin = origin;
            int poseIndex = NearestPose(rotation, out float offAngle);
            report.NearestPose = probe.Profiles[poseIndex].Pose.Label;
            report.PoseOffAngle = offAngle;

            // The item's shape exactly as it sits, and the painted runs under the columns it reaches.
            var rigidbody = body.GetBody();
            var exactProfile = MeasureProfile(probe, rotation, rigidbody ? rigidbody.centerOfMass : probe.LocalBounds.center);
            // Its real lowest point, from the measured shape (a tilted bounding box's corner can sit well below it).
            float bottom = origin.y + exactProfile.BaseY;
            report.Bottom = bottom;
            int exactIx = Mathf.RoundToInt((origin.x + exactProfile.MinX) / VoxelSize);
            int exactIz = Mathf.RoundToInt((origin.z + exactProfile.MinZ) / VoxelSize);
            var missing = new List<string>();
            MeasureCoverage(area, exactProfile, exactIx, exactIz, origin.y, missing,
                out report.FootprintColumns, out report.UnpaintedColumns, out float paintedRest, out float paintedStart);
            report.PaintedSummary = (report.FootprintColumns - report.UnpaintedColumns) + "/" + report.FootprintColumns + " columns painted"
                + (missing.Count > 0 ? " (unpainted e.g. " + string.Join(" ", missing.ToArray()) + ")" : "")
                + (paintedRest > float.MinValue
                    ? "; painted floors put its bottom at " + (paintedRest + exactProfile.BaseY).ToString("0.00")
                        + " (actual " + bottom.ToString("0.00") + "), ceilings and stack height " + area.StackHeight.ToString("0.0")
                        + "m allow it up to " + (paintedStart + exactProfile.BaseY).ToString("0.00")
                    : "");

            // Physics at the exact spot, independent of the painting: start just above where it sits.
            lastSweepHit = null;
            report.ExactOutcome = EvaluatePose(origin.x, origin.z, exactProfile, origin.y - 0.3f, origin.y + ConfirmStartLift,
                out var exact, out float exactRest, out int exactSupport);
            report.ExactDetail = DescribeAttempt(exactRest, exactProfile, exactSupport, bottom)
                + (report.ExactOutcome == Outcome.Valid ? " contacts=" + exact.SideContacts : "");

            // The nearest candidate the solver generates: snapped to its nearest pose and the 10cm grid.
            var profile = probe.Profiles[poseIndex];
            int ix = Mathf.RoundToInt((origin.x + profile.MinX) / VoxelSize);
            int iz = Mathf.RoundToInt((origin.z + profile.MinZ) / VoxelSize);
            var candidate = new Candidate { Ix = ix, Iz = iz, PoseIndex = poseIndex };
            Vector2 gridOrigin = CandidateOrigin(candidate);
            report.GridOffset = new Vector2(gridOrigin.x - origin.x, gridOrigin.y - origin.z).magnitude;

            lastCoarseFailure = null;
            if (!TryCoarseFit(area, profile, ix, iz, origin.y, out float floorRest, out float estimatedRest, out float startY))
            {
                report.GridCoarse = "rejected: " + lastCoarseFailure;
                return report;
            }

            candidate.FloorRest = floorRest;
            candidate.EstimatedRest = estimatedRest;
            candidate.StartY = startY;
            report.GridCoarse = "kept (floorRestBottom=" + (floorRest + profile.BaseY).ToString("0.00")
                + " estRest=" + (estimatedRest > floorRest + 50f ? "on-cargo-no-room, tried last" : (estimatedRest + profile.BaseY).ToString("0.00"))
                + " startBottom=" + (startY + profile.BaseY).ToString("0.00") + ")";

            lastSweepHit = null;
            report.GridOutcome = Evaluate(candidate, out var gridPlacement, out float gridRest, out int gridSupport);
            report.GridDetail = DescribeAttempt(gridRest, profile, gridSupport, bottom)
                + (report.GridOutcome == Outcome.Valid ? " contacts=" + gridPlacement.SideContacts : "");
            return report;
        }

        // The solver pose closest to a rotation, and how far off it is. A round item's pose only fixes where its axis of
        // symmetry points; a box-like item's fixes which of its axes point up and along z (either way along each).
        private static int NearestPose(Quaternion rotation, out float offAngle)
        {
            int best = 0;
            offAngle = float.MaxValue;
            for (int i = 0; i < probe.Profiles.Count; i++)
            {
                var pose = probe.Profiles[i].Pose;
                float angle;
                if (pose.SymmetryAxis != Vector3.zero)
                {
                    angle = AxisAngle(rotation * pose.SymmetryAxis, pose.Rotation * pose.SymmetryAxis);
                }
                else
                {
                    angle = Mathf.Max(
                        AxisAngle(rotation * pose.LocalUp, Vector3.up),
                        AxisAngle(rotation * pose.LocalForward, Vector3.forward));
                }

                if (angle < offAngle)
                {
                    offAngle = angle;
                    best = i;
                }
            }

            return best;
        }

        // The angle between two directions, treating a direction and its opposite as the same axis.
        private static float AxisAngle(Vector3 a, Vector3 b)
        {
            float angle = Vector3.Angle(a, b);
            return Mathf.Min(angle, 180f - angle);
        }

        // Like TryCoarseFit, but counts every column instead of stopping at the first problem.
        private static void MeasureCoverage(CargoArea area, PoseProfile profile, int ix, int iz, float referenceY, List<string> missing,
            out int columns, out int unpainted, out float floorRest, out float startY)
        {
            columns = 0;
            unpainted = 0;
            floorRest = float.MinValue;
            startY = float.MaxValue;

            for (int dx = 0; dx < profile.CellsX; dx++)
            {
                for (int dz = 0; dz < profile.CellsZ; dz++)
                {
                    int index = dx * profile.CellsZ + dz;
                    if (float.IsNaN(profile.Bottom[index]))
                        continue;

                    columns++;
                    float sliceMin = referenceY + profile.Bottom[index], sliceMax = referenceY + profile.Top[index];
                    bool found = false;
                    float bestOverlap = 0f;
                    var chosen = default(CargoArea.Run);
                    if (area.TryGetColumn(ix + dx, iz + dz, out var runs))
                    {
                        foreach (var run in runs)
                        {
                            float overlap = Mathf.Min(run.CeilingY, sliceMax) - Mathf.Max(run.FloorY, sliceMin - 0.1f);
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
                            missing.Add("(" + (ix + dx) + "," + (iz + dz) + ")");
                        continue;
                    }

                    floorRest = Mathf.Max(floorRest, chosen.FloorY - profile.Bottom[index]);
                    startY = Mathf.Min(startY, area.EffectiveCeiling(chosen) - profile.Top[index]);
                }
            }
        }

        private static string DescribeAttempt(float restY, PoseProfile profile, int supportHits, float actualBottom)
        {
            return (float.IsNaN(restY) ? "" : "restBottom=" + (restY + profile.BaseY).ToString("0.00")
                    + " (actual " + actualBottom.ToString("0.00") + ") ")
                + "support=" + supportHits + "/" + profile.BaseSamples.Count
                + (lastSweepHit != null ? " hit=" + lastSweepHit : "");
        }

        private static void WriteClaim(ClaimReport r)
        {
            string gridOutcome = r.GridOutcome.HasValue ? r.GridOutcome.Value.ToString() : "not tried";
            CrewDebugLog.Ok(Phase, "CLAIM item='" + r.Item + "' vessel='" + r.Vessel + "' origin=" + Format(r.Origin)
                + " bottom=" + r.Bottom.ToString("0.00") + " shape=" + r.Shape);
            CrewDebugLog.Ok(Phase, "  pose: nearest solver pose '" + r.NearestPose + "', " + r.PoseOffAngle.ToString("0.0") + " degrees off");
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
                            "time", "vessel", "item", "shape", "origin", "bottom", "nearestPose", "poseOffAngle",
                            "footprintColumns", "unpaintedColumns", "painted", "exactOutcome", "exactDetail",
                            "gridOffset", "gridCoarse", "gridOutcome", "gridDetail"
                        }));

                    writer.WriteLine(string.Join("\t", new[]
                    {
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), r.Vessel, r.Item, r.Shape, Format(r.Origin),
                        r.Bottom.ToString("0.000"), r.NearestPose, r.PoseOffAngle.ToString("0.0"),
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
