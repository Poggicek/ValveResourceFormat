using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Movement;
using ValveResourceFormat.Renderer.Movement.Demo;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using ValveKeyValue;
using ValveResourceFormat.Utils;

namespace Tests.Renderer
{
    public class DemoProbeTemp
    {
        private const string Out = @"C:\Users\ADMIN\AppData\Local\Temp\claude\C--ValveResourceFormat-cs2-movement\ab298336-4575-460a-87e6-74d4ed049d13\scratchpad\";

        [Test]
        public async Task Probe()
        {
            var demoPath = Environment.GetEnvironmentVariable("VRF_DEMO") ?? @"g:\Documents\Downloads\texturebug.dem";
            var log = new StringBuilder();
            var cmds = new StringBuilder();
            var decoder = new UserCommandDecoder();
            var dumped = false;
            DemoParser? parser = null;

            parser = new DemoParser
            {
                EntitiesUpdated = tick =>
                {
                    if (tick == 41400)
                    {
                        var list = new StringBuilder();
                        foreach (var e in parser!.Entities.Values.Where(e => e.Class.Name.Contains("Player", StringComparison.Ordinal)))
                        {
                            list.AppendLine(CultureInfo.InvariantCulture, $"{e.Index} {e.Class.Name} pawn={e.Get("m_hPlayerPawn", 0u):X} pawn2={e.Get("m_hPawn", 0u):X} name={e.Get("m_iszPlayerName", "")} controller={e.Get("m_hController", 0u):X}");
                        }
                        File.WriteAllText(Out + "entities.txt", list.ToString());
                        var more = new StringBuilder();
                        var c1 = parser.Entities[1];
                        more.AppendLine("CONTROLLER " + c1.Class.Name);
                        Dump(more, c1.Class.Serializer, "", 0);
                        var p1 = parser.Entities[(int)(c1.Get("m_hPlayerPawn", 0u) & DemoEntity.HandleIndexMask)];
                        var w = parser.Entities[(int)(p1.Get("m_pWeaponServices.m_hActiveWeapon", 0u) & DemoEntity.HandleIndexMask)];
                        more.AppendLine("WEAPON " + w.Class.Name + " subclass " + w.Get("m_nSubclassID", 0u).ToString("X", CultureInfo.InvariantCulture));
                        Dump(more, w.Class.Serializer, "", 0);
                        File.WriteAllText(Out + "controller_weapon.txt", more.ToString());
                    }
                    var controller = parser!.Entities.GetValueOrDefault(1);
                    var pawnHandle = controller?.Get("m_hPlayerPawn", 0xFFFFFFFFu) ?? 0xFFFFFFFFu;
                    var pawn = parser.Entities.GetValueOrDefault((int)(pawnHandle & DemoEntity.HandleIndexMask));
                    if (pawn == null)
                    {
                        return;
                    }

                    if (!dumped)
                    {
                        dumped = true;
                        var tree = new StringBuilder();
                        Dump(tree, pawn.Class.Serializer, "", 0);
                        File.WriteAllText(Out + "pawn_fields.txt", tree.ToString());

                        var values = new StringBuilder();
                        foreach (var (path, value) in pawn.Values.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
                        {
                            values.AppendLine(CultureInfo.InvariantCulture, $"{path} {Name(pawn.Class, path)} = {Format(value)}");
                        }
                        File.WriteAllText(Out + "pawn_values.txt", values.ToString());
                    }

                    var cx = pawn.Get("CBodyComponent.m_skeletonInstance.m_vecOrigin.m_cellX", 0u);
                    var cy = pawn.Get("CBodyComponent.m_skeletonInstance.m_vecOrigin.m_cellY", 0u);
                    var cz = pawn.Get("CBodyComponent.m_skeletonInstance.m_vecOrigin.m_cellZ", 0u);
                    var vx = pawn.Get("CBodyComponent.m_skeletonInstance.m_vecOrigin.m_vecX", 0f);
                    var vy = pawn.Get("CBodyComponent.m_skeletonInstance.m_vecOrigin.m_vecY", 0f);
                    var vz = pawn.Get("CBodyComponent.m_skeletonInstance.m_vecOrigin.m_vecZ", 0f);
                    var vel = new Vector3(pawn.Get("m_vecVelocity.m_vecX", 0f), pawn.Get("m_vecVelocity.m_vecY", 0f), pawn.Get("m_vecVelocity.m_vecZ", 0f));
                    log.AppendLine(CultureInfo.InvariantCulture, $"{tick},{cx * 512f - 16384f + vx},{cy * 512f - 16384f + vy},{cz * 512f - 16384f + vz},{vel.X},{vel.Y},{vel.Z},{pawn.Get("m_fFlags", 0u)}");
                },
                UserCommand = c =>
                {
                    var cmd = decoder.Decode(c);
                    cmds.AppendLine(CultureInfo.InvariantCulture, $"{c.ServerTickExecuted},{c.CommandNumber},{cmd.ForwardMove},{cmd.LeftMove},{cmd.Buttons.Held},{cmd.Buttons.Changed},{cmd.ViewAngles.X},{cmd.ViewAngles.Y},{cmd.SubtickMoves.Count}");
                },
            };

            using (var stream = File.OpenRead(demoPath))
            {
                parser.Parse(stream);
            }

            File.WriteAllText(Out + "vrf_pos.csv", log.ToString());
            File.WriteAllText(Out + "vrf_cmds.csv", cmds.ToString());
            await Assert.That(log.Length).IsGreaterThan(0);
        }

        [Test]
        public async Task Replay()
        {
            var demoPath = Environment.GetEnvironmentVariable("VRF_DEMO") ?? @"g:\Documents\Downloads\texturebug.dem";
            DemoMovementRecording recording;
            using (var stream = File.OpenRead(demoPath))
            {
                recording = DemoMovementRecording.Load(stream);
            }

            var vpkPath = $@"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\maps\{recording.MapName}.vpk";
            using var package = new Package();
            package.Read(vpkPath);
            package.ReadEntry(package.FindEntry($"maps/{recording.MapName}/world_physics.vmdl_c")!, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var phys = (PhysAggregateData)resource.GetBlockByType(BlockType.PHYS)!;

            using var fileLoader = new GameFileLoader(package, vpkPath);
            using var context = new RendererContext(fileLoader, NullLogger.Instance);
            var scene = new Scene(context);
            var world = new PhysicsWorld();
            world.Add(scene, new Rubikon(phys, fileLoader.BuildMeshWings), Matrix4x4.Identity);
            var collision = new MovementCollision { World = world };

            var speeds = WeaponSpeeds(fileLoader);
            var cmdDump = new StringBuilder();
            foreach (var hash in recording.States.Values.Select(st => st.WeaponSubclass).Distinct())
            {
                cmdDump.AppendLine(CultureInfo.InvariantCulture, $"weapon subclass {hash:X} known {speeds.ContainsKey(hash)}");
            }
            foreach (var dc in recording.Commands.Take(400))
            {
                var c = dc.Command;
                cmdDump.AppendLine(CultureInfo.InvariantCulture, $"{dc.ServerTick} #{c.CommandNumber} held {c.Buttons.Held:X} chg {c.Buttons.Changed:X} chg2 {c.Buttons.ChangedTwice:X} fwd {c.ForwardMove} left {c.LeftMove} up {c.UpMove} ang {c.ViewAngles}");
                foreach (var m in c.SubtickMoves)
                {
                    cmdDump.AppendLine(CultureInfo.InvariantCulture, $"    when {m.When} button {m.Button} pressed {m.Pressed} af {m.AnalogForwardDelta} al {m.AnalogLeftDelta} pd {m.PitchDelta} yd {m.YawDelta}");
                }
            }
            File.WriteAllText(Out + "cmd_dump.txt", cmdDump.ToString());
            var log = new StringBuilder();
            log.AppendLine(CultureInfo.InvariantCulture, $"{recording.MapName} player {recording.Player} commands {recording.Commands.Count} states {recording.States.Count} wings {fileLoader.BuildMeshWings} knife hash {StringToken.Get("weapon_knife"):X}");

            var replay = new DemoReplay(recording, new MovementServices(new MovementVars(), collision) { Surfaces = SurfacePropertyTable.Load(fileLoader) }, s => speeds.TryGetValue(s.WeaponSubclass, out var v) ? v[Math.Min(s.WeaponMode, 1)] : null);

            var idx41300 = recording.Commands.ToList().FindIndex(c => c.ServerTick == 41300);
            replay.Seek(idx41300);
            var stateDump = new StringBuilder();
            replay.Services.VisitState(new PrintVisitor(stateDump));
            stateDump.AppendLine("---- networked before");
            foreach (var (k, v) in replay.StateBefore(idx41300)!.Values) { stateDump.AppendLine(CultureInfo.InvariantCulture, $"{k} = {Format(v)} ({v.GetType().Name})"); }
            var st = replay.Step();
            stateDump.AppendLine(CultureInfo.InvariantCulture, $"---- after step {st.Origin} {st.Velocity} want {st.Expected?.Velocity}");
            replay.Services.VisitState(new PrintVisitor(stateDump));
            File.WriteAllText(Out + "state_dump.txt", stateDump.ToString());

            var window = new StringBuilder();
            for (var t = 41318; t <= 41340; t++)
            {
                var ns = recording.States[t];
                window.AppendLine(CultureInfo.InvariantCulture, $"{t} noclip {ns.Values.GetValueOrDefault("NoClip")} org {ns.Origin} vel {ns.Velocity} ground {ns.GroundEntityIndex} flags {ns.Values.GetValueOrDefault("Flags")} base {Format(ns.Values.GetValueOrDefault("BaseVelocity") ?? 0)} move {ns.Values.GetValueOrDefault("MoveType")} fall {ns.Values.GetValueOrDefault("FallVelocity")} landed {ns.Values.GetValueOrDefault("LastLandedTick")} jump {ns.Values.GetValueOrDefault("LastJumpTick")} jvz {ns.Values.GetValueOrDefault("LastJumpVelocityZ")} water {ns.Values.GetValueOrDefault("WaterLevelFraction")} grav {ns.Values.GetValueOrDefault("GravityScale")} stash {ns.Values.GetValueOrDefault("UseFrictionStashedSpeed")}");
            }
            // Sweep the hull from each server position to the next, and 64 units further along the motion
            for (var t = 41320; t <= 41336; t++)
            {
                var a = recording.States[t - 1].Origin;
                var b = recording.States[t].Origin;
                var dir = Vector3.Normalize(new Vector3(b.X - a.X, b.Y - a.Y, 0f));
                var tr = collision.TraceHull(a, b, MovementServices.HullMin, MovementServices.HullMax, Rubikon.Cs2PlayerCollisionFilter);
                var far = collision.TraceHull(a, a + dir * 64f, MovementServices.HullMin, MovementServices.HullMax, Rubikon.Cs2PlayerCollisionFilter);
                var stuck = collision.TraceHull(b, b, MovementServices.HullMin, MovementServices.HullMax, Rubikon.Cs2PlayerCollisionFilter);
                window.AppendLine(CultureInfo.InvariantCulture, $"{t}: step frac {tr.Fraction} n {tr.Normal} startsolid {tr.StartSolid} | ahead64 frac {far.Fraction} n {far.Normal} end {far.EndPos} | at server pos stuck {stuck.StartSolid}");
            }
            object? lastMove = null;
            foreach (var st2 in recording.States.Values.OrderBy(x => x.ServerTick))
            {
                var mt = st2.Values.GetValueOrDefault("NoClip");
                if (!Equals(mt, lastMove)) { window.AppendLine(CultureInfo.InvariantCulture, $"movetype {mt} from tick {st2.ServerTick} at {st2.Origin}"); lastMove = mt; }
            }
            var rub = new Rubikon(phys, fileLoader.BuildMeshWings);
            var lo = new Vector3(-420f, -680f, -430f);
            var hi = new Vector3(-310f, -520f, -290f);
            bool Inside(Vector3 v) => v.X >= lo.X && v.Y >= lo.Y && v.Z >= lo.Z && v.X <= hi.X && v.Y <= hi.Y && v.Z <= hi.Z;
            void DumpMesh(string label, Rubikon.PhysicsMeshData mesh)
            {
                var n = 0;
                var lines = new StringBuilder();
                for (var i = 0; i < mesh.Triangles.Length; i++)
                {
                    var tri = mesh.Triangles[i];
                    var a = mesh.VertexPositions[tri.X]; var b = mesh.VertexPositions[tri.Y]; var c = mesh.VertexPositions[tri.Z];
                    if (!Inside(a) && !Inside(b) && !Inside(c)) { continue; }
                    n++;
                    var nrm = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                    if (lines.Length < 6000) { lines.AppendLine(CultureInfo.InvariantCulture, $"      tri {i} n {nrm} {a} {b} {c}"); }
                }
                if (n > 0)
                {
                    window.AppendLine(CultureInfo.InvariantCulture, $"{label} tags [{string.Join(",", mesh.InteractAs)}] excl [{string.Join(",", mesh.InteractExclude)}] flags {mesh.Flags} tris in box {n} wings {mesh.Wings.Length}");
                    window.Append(lines);
                }
            }
            for (var m = 0; m < rub.Meshes.Length; m++) { DumpMesh($"mesh {m}", rub.Meshes[m]); }
            for (var h = 0; h < rub.Hulls.Length; h++)
            {
                var hull = rub.Hulls[h];
                if (hull.Max.X < lo.X || hull.Min.X > hi.X || hull.Max.Y < lo.Y || hull.Min.Y > hi.Y || hull.Max.Z < lo.Z || hull.Min.Z > hi.Z) { continue; }
                window.AppendLine(CultureInfo.InvariantCulture, $"hull {h} tags [{string.Join(",", hull.InteractAs)}] excl [{string.Join(",", hull.InteractExclude)}] {hull.Min} {hull.Max}");
            }
            for (var c = 0; c < rub.Compounds.Length; c++)
            {
                var comp = rub.Compounds[c];
                for (var m = 0; m < comp.Meshes.Length; m++) { DumpMesh($"compound {c} mesh {m} ctags [{string.Join(",", comp.InteractAs)}]", comp.Meshes[m]); }
                foreach (var hull in comp.Hulls)
                {
                    if (hull.Max.X < lo.X || hull.Min.X > hi.X || hull.Max.Y < lo.Y || hull.Min.Y > hi.Y || hull.Max.Z < lo.Z || hull.Min.Z > hi.Z) { continue; }
                    window.AppendLine(CultureInfo.InvariantCulture, $"compound {c} hull tags [{string.Join(",", hull.InteractAs)}] {hull.Min} {hull.Max}");
                }
            }
            File.WriteAllText(Out + "window.txt", window.ToString());

            // Free run from the first command, then per-tick from the networked state each time
            var start = Enumerable.Range(0, recording.Commands.Count).First(i => replay.StateBefore(i) != null);
            replay.Seek(start);
            var firstBad = -1;
            var matched = 0;
            while (!replay.Finished)
            {
                var step = replay.Step();
                if (step.Matches) { matched++; } else if (firstBad < 0) { firstBad = step.Index; log.AppendLine(CultureInfo.InvariantCulture, $"free run first mismatch at {step.Index} tick {step.ServerTick} err {step.OriginError} vel {step.VelocityError} got {step.Origin} {step.Velocity} want {step.Expected!.Origin} {step.Expected.Velocity}"); }
            }
            log.AppendLine(CultureInfo.InvariantCulture, $"free run: {matched} of {recording.Commands.Count - start} exact, final error {Vector3.Distance(replay.Services.AbsOrigin, recording.States.Values.MaxBy(s => s.ServerTick)!.Origin)}");

            var perTickBad = 0;
            var perTickChecked = 0;
            replay.Seek(start);
            for (var i = start; i < recording.Commands.Count; i++)
            {
                var step = replay.Step(resync: true);
                if (step.Expected == null) { continue; }
                perTickChecked++;
                if (!step.Matches)
                {
                    perTickBad++;
                    if (perTickBad <= 40)
                    {
                        var c = recording.Commands[i].Command;
                        log.AppendLine(CultureInfo.InvariantCulture, $"tick {step.ServerTick} idx {i} err {step.OriginError:0.######} vel {step.VelocityError:0.######} got {step.Origin} {step.Velocity} want {step.Expected.Origin} {step.Expected.Velocity} held {c.Buttons.Held:X} subticks {c.SubtickMoves.Count} fwd {c.ForwardMove} left {c.LeftMove} flags {step.Expected.Values.GetValueOrDefault("Flags")}");
                    }
                }
            }
            log.AppendLine(CultureInfo.InvariantCulture, $"per tick: {perTickChecked - perTickBad} of {perTickChecked} exact");
            var buckets = new Dictionary<string, int>();
            var samples = new Dictionary<string, List<string>>();
            replay.Seek(start);
            var trace = new StringBuilder();
            for (var i = start; i < recording.Commands.Count; i++)
            {
                var noclipBefore = replay.StateBefore(i)!.Values.GetValueOrDefault("NoClip") is true;
                var step = replay.Step(resync: true);
                if (step.Expected != null)
                {
                    var ex = step.Expected;
                    trace.AppendLine(CultureInfo.InvariantCulture, $"{step.ServerTick} {(step.Matches ? "ok " : "BAD")} dz {step.Origin.Z - ex.Origin.Z} dxy {(step.Origin - ex.Origin) with { Z = 0 }} dv {step.Velocity - ex.Velocity} ground {ex.GroundEntityIndex} noclip {ex.Values.GetValueOrDefault("NoClip")} jump {ex.Values.GetValueOrDefault("LastJumpTick")} z {ex.Origin.Z} vz {ex.Velocity.Z}");
                }
                if (step.Expected == null || step.Matches) { continue; }
                var noclipAfter = step.Expected.Values.GetValueOrDefault("NoClip") is true;
                var d = step.Origin - step.Expected.Origin;
                string axes = (d.X != 0 ? "x" : "") + (d.Y != 0 ? "y" : "") + (d.Z != 0 ? "z" : "");
                string key;
                var firstTick = recording.Commands[0].ServerTick;
                var jumpBeforeDemo = replay.StateBefore(i)!.Values.GetValueOrDefault("LastJumpTick") is int lj && lj < firstTick;
                if (noclipBefore != noclipAfter) { key = "noclip toggled"; }
                else if (jumpBeforeDemo && !noclipBefore && step.Expected.GroundEntityIndex < 0) { key = "air, last jump before demo"; }
                else if (step.VelocityError == 0 && step.OriginError < 0.001f) { key = $"ulp origin {axes} vel exact {(noclipBefore ? "noclip" : step.Expected.GroundEntityIndex >= 0 ? "ground" : "air")}"; }
                else if (step.OriginError < 0.01f && step.VelocityError < 0.1f) { key = $"small {(noclipBefore ? "noclip" : step.Expected.GroundEntityIndex >= 0 ? "ground" : "air")}"; }
                else { key = $"large {(noclipBefore ? "noclip" : step.Expected.GroundEntityIndex >= 0 ? "ground" : "air")}"; }
                buckets[key] = buckets.GetValueOrDefault(key) + 1;
                if (!samples.TryGetValue(key, out var list)) { list = []; samples[key] = list; }
                if (list.Count < 6)
                {
                    var c = recording.Commands[i].Command;
                    list.Add(string.Create(CultureInfo.InvariantCulture, $"    tick {step.ServerTick} idx {i} d {d} dv {step.Velocity - step.Expected.Velocity} got {step.Origin} {step.Velocity} want {step.Expected.Origin} {step.Expected.Velocity} held {c.Buttons.Held:X} sub {c.SubtickMoves.Count} fwd {c.ForwardMove} left {c.LeftMove}"));
                }
            }
            File.WriteAllText(Out + "trace.txt", trace.ToString());
            var pred = new StringBuilder();
            for (var i = 1; i < recording.Commands.Count; i++)
            {
                var tick = recording.Commands[i].ServerTick;
                if (tick < 42837 || tick > 42885) { continue; }
                var before = recording.States[tick - 1];
                var jt = (int)before.Values["LastJumpTick"];
                var jf = (float)before.Values["LastJumpFrac"];
                var jv = (float)before.Values["LastJumpVelocityZ"];
                var tb = (int)before.Values["TickBase"];
                foreach (var tickCount in new[] { tb - 2, tb - 1, tb })
                {
                    var t = (new TickFraction(tickCount, 0f) - new TickFraction(jt, jf)).ToSeconds();
                    var p2 = Math.Clamp(jv - ((800f * 1f) * t), -16384f, 16384f);
                    pred.AppendLine(CultureInfo.InvariantCulture, $"{tick} tickbase {tb} mvtick {tickCount} netvz {before.Velocity.Z} pred {p2} q {QuantizedFloat.Velocity.Quantize(p2)} eq {QuantizedFloat.Velocity.Quantize(p2) == before.Velocity.Z} jumpfrac {jf}");
                }
            }
            File.WriteAllText(Out + "pred.txt", pred.ToString());
            foreach (var (k, v) in buckets.OrderByDescending(b => b.Value))
            {
                log.AppendLine(CultureInfo.InvariantCulture, $"{v,5} {k}");
                foreach (var line in samples[k]) { log.AppendLine(line); }
            }
            File.WriteAllText(Out + "replay.txt", log.ToString());
            await Assert.That(perTickChecked).IsGreaterThan(0);
        }

        private sealed class PrintVisitor(StringBuilder sb) : IMovementStateVisitor
        {
            public void Visit(string name, ref float value) => sb.AppendLine(CultureInfo.InvariantCulture, $"{name} = {value}");
            public void Visit(string name, ref int value) => sb.AppendLine(CultureInfo.InvariantCulture, $"{name} = {value}");
            public void Visit(string name, ref bool value) => sb.AppendLine(CultureInfo.InvariantCulture, $"{name} = {value}");
            public void Visit(string name, ref ulong value) => sb.AppendLine(CultureInfo.InvariantCulture, $"{name} = {value:X}");
            public void Visit(string name, ref Vector2 value) => sb.AppendLine(CultureInfo.InvariantCulture, $"{name} = {value}");
            public void Visit(string name, ref Vector3 value) => sb.AppendLine(CultureInfo.InvariantCulture, $"{name} = {value}");
        }

        private static Dictionary<uint, float[]> WeaponSpeeds(GameFileLoader loader)
        {
            var result = new Dictionary<uint, float[]>();
            using var resource = loader.LoadFileCompiled("scripts/weapons.vdata");
            if (resource?.DataBlock is not BinaryKV3 kv3)
            {
                return result;
            }
            foreach (var (name, weapon) in kv3.Data.Root)
            {
                if (weapon.ValueType == KVValueType.Collection && weapon.GetArray<float>("m_flMaxSpeed") is { Length: >= 2 } speed)
                {
                    result[StringToken.Get(name)] = speed;
                }
            }
            return result;
        }

        private static string Name(ServerClass c, FieldPath path)
        {
            var field = c.Serializer.Fields[path[0]];
            var name = field.Name;
            for (var i = 1; i < path.Length; i++)
            {
                field = field.Child(path[i]);
                name += "." + field.Name;
            }
            return name;
        }

        private static string Format(object value) => value switch
        {
            Vector3 v => $"({v.X}, {v.Y}, {v.Z})",
            float[] a => string.Join(", ", a),
            byte[] b => Convert.ToHexString(b),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };

        private static void Dump(StringBuilder sb, Serializer s, string indent, int depth)
        {
            for (var i = 0; i < s.Fields.Length; i++)
            {
                var f = s.Fields[i];
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{i} {f.Name} : {f.Type} [{f.Kind}] {f.Decoder?.Encoding} {f.Element?.Decoder?.Encoding}");
                var child = f.Serializer ?? f.Element?.Serializer;
                if (child != null && depth < 3)
                {
                    Dump(sb, child, indent + "  ", depth + 1);
                }
            }
        }
    }
}
