using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using OpenTK;
using OpenTK.Graphics.OpenGL;

namespace Tests.Renderer.Golden
{
    /// <summary>
    /// Answers "which direct OpenGL calls does the renderer still make, and from where?" by measurement,
    /// on a run that has no OpenGL context at all.
    ///
    /// <para>The renderer issues several hundred <c>GL.</c> calls outside the OpenGL RHI backend. On a
    /// Vulkan device none of them can work, and the whole question of what blocks a Vulkan golden run is
    /// "which of those does a scene actually reach, and in what order". Reading the source cannot answer
    /// that -- most of those call sites are behind conditions -- and simply running without a context
    /// answers it once and fatally, because an unloaded OpenTK entry point is a null function pointer and
    /// calling it takes the process down with an access violation rather than an exception.</para>
    ///
    /// <para>So the entry points are loaded, but with a trap. <see cref="Install"/> hands OpenTK a
    /// bindings context whose <c>GetProcAddress</c> returns, for every one of the several thousand GL
    /// functions, a distinct managed thunk that records the call and returns zero. Nothing crashes,
    /// nothing renders, and the record is exact: entry point name, call count, and the renderer source
    /// file and line that issued it.</para>
    ///
    /// <para><b>The trap deliberately does not stop the run at the first call.</b> A hard failure would
    /// name one blocker; letting every call no-op enumerates the whole surface a scene touches, which is
    /// the deliverable. The consequence to keep in mind when reading a report: the renderer proceeds on
    /// zeroed handles and garbage query results, so any exception raised after the first trapped call is
    /// a <em>consequence</em> of the trap, not an independent finding. The trapped call list is the
    /// evidence; the exception is only where the house of cards fell over.</para>
    /// </summary>
    /// <remarks>
    /// Returning zero is chosen rather than an arbitrary value: it reads as <c>GL_NO_ERROR</c> from
    /// <c>glGetError</c>, as a null pointer from <c>glGetString</c> and <c>glMapNamedBuffer</c>, and as
    /// the "no object" name from every <c>glCreate*</c>, all of which the renderer is at least written to
    /// notice. A thunk taking no arguments is safe against callers that pass many, because every calling
    /// convention on the platforms this runs on is caller-cleanup.
    /// </remarks>
    internal static class GLCallTrap
    {
        /// <summary>
        /// The signature every trapped entry point is called through.
        /// </summary>
        /// <returns>Zero, in the integer return register.</returns>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint TrapThunk();

        /// <summary>
        /// How many distinct call sites are recorded per entry point. A bound rather than a limit anyone
        /// should reach: <c>glEnable</c> has the most in the renderer and is well under it.
        /// </summary>
        private const int MaxSitesPerEntryPoint = 64;

        /// <summary>Keeps the thunks alive: a collected delegate leaves OpenTK holding a dangling pointer.</summary>
        private static readonly List<TrapThunk> Thunks = [];

        private static readonly ConcurrentDictionary<string, TrappedEntryPoint> EntryPoints = new(StringComparer.Ordinal);

        private static bool Installed;

        /// <summary>
        /// Which stage of the harness is running, so a trapped call can be attributed to one. Set by the
        /// Vulkan harness around each step it takes.
        /// </summary>
        public static string CurrentStage { get; set; } = "(none)";

        /// <summary>
        /// Points OpenTK's OpenGL bindings at the trap. Irreversible within a process, and never called on
        /// a run that has a real context.
        /// </summary>
        public static void Install()
        {
            if (Installed)
            {
                return;
            }

            Installed = true;

            GL.LoadBindings(new TrapBindingsContext());
        }

        /// <summary>How many direct GL calls have been trapped since the last <see cref="Reset"/>.</summary>
        public static int TotalCalls => EntryPoints.Values.Sum(static entry => entry.Count);

        /// <summary>How many direct GL calls have been trapped over the whole run.</summary>
        public static int TotalCallsThisRun => EntryPoints.Values.Sum(static entry => entry.TotalCount);

        /// <summary>
        /// Groups everything trapped by the renderer source file and line that issued it, which is the
        /// shape that says how much work each blocking file represents.
        /// </summary>
        /// <param name="wholeRun">Whether to count the whole run rather than since the last reset.</param>
        /// <remarks>
        /// Counted per call site rather than per entry point, and the difference is not cosmetic. Most GL
        /// functions here are called from several files -- <c>glTextureParameteri</c> from the framebuffer,
        /// the render texture and the material loader alike -- so attributing an entry point's whole count
        /// to every file that calls it inflated the total to nearly twice the number of calls actually
        /// made. The per-file figures below sum to <see cref="TotalCallsThisRun"/> exactly.
        /// </remarks>
        public static string ReportByFile(bool wholeRun = false)
        {
            var byFile = new Dictionary<string, FileTally>(StringComparer.Ordinal);

            foreach (var entry in EntryPoints.Values)
            {
                foreach (var (site, calls) in entry.SiteCounts(wholeRun))
                {
                    if (calls == 0)
                    {
                        continue;
                    }

                    if (!byFile.TryGetValue(site.File, out var tally))
                    {
                        tally = new FileTally();
                        byFile[site.File] = tally;
                    }

                    tally.Add(entry.Name, calls, site);
                }
            }

            if (byFile.Count == 0)
            {
                return "  no direct OpenGL call was reached.";
            }

            var lines = new List<string>();

            foreach (var (file, tally) in byFile.OrderByDescending(static pair => pair.Value.Calls).ThenBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                lines.Add($"  {file}: {tally.Calls:N0} calls from {tally.SiteCount} site(s), {tally.EntryPointCount} entry point(s)");

                foreach (var line in tally.Describe())
                {
                    lines.Add("      " + line);
                }
            }

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>Forgets everything recorded, so the next scene starts from nothing.</summary>
        public static void Reset()
        {
            foreach (var entry in EntryPoints.Values)
            {
                entry.Reset();
            }
        }

        /// <summary>One GL function, and where the renderer called it from.</summary>
        internal sealed class TrappedEntryPoint(string name)
        {
            private readonly Lock Guard = new();
            private readonly Dictionary<CallSite, SiteTally> SiteTallies = [];

            /// <summary>The GL entry point name, as OpenTK asked for it.</summary>
            public string Name { get; } = name;

            /// <summary>How many times it has been called since the last <see cref="Reset"/>.</summary>
            public int Count { get; private set; }

            /// <summary>How many times it has been called over the whole run.</summary>
            public int TotalCount { get; private set; }

            /// <summary>How many calls came from each site.</summary>
            /// <param name="wholeRun">Whether to count the whole run rather than since the last reset.</param>
            public IEnumerable<(CallSite Site, int Calls)> SiteCounts(bool wholeRun)
            {
                using var _ = Guard.EnterScope();

                return SiteTallies
                    .Select(pair => (pair.Key, wholeRun ? pair.Value.Total : pair.Value.Current))
                    .ToArray();
            }

            /// <summary>Records one call. Runs inside the trapped GL call itself.</summary>
            /// <remarks>
            /// The stack is walked on every call, not only on the first sighting of an entry point. Walking
            /// once was cheaper and wrong: it can say <em>that</em> a file calls a function but not how
            /// often, so a file the frame touches once and a file it touches a thousand times were reported
            /// identically. A run trapping a few thousand calls can afford the walk.
            /// </remarks>
            public nint Note()
            {
                var site = Capture();

                using var _ = Guard.EnterScope();

                Count++;
                TotalCount++;

                if (!SiteTallies.TryGetValue(site, out var tally))
                {
                    if (SiteTallies.Count >= MaxSitesPerEntryPoint)
                    {
                        return 0;
                    }

                    tally = new SiteTally();
                    SiteTallies[site] = tally;
                }

                tally.Current++;
                tally.Total++;

                return 0;
            }

            /// <summary>
            /// Clears the per-scene counts.
            /// </summary>
            /// <remarks>The run totals survive, so the end-of-run report covers every scene rather than
            /// only the last one.</remarks>
            public void Reset()
            {
                using var _ = Guard.EnterScope();

                Count = 0;

                foreach (var tally in SiteTallies.Values)
                {
                    tally.Current = 0;
                }
            }

            private sealed class SiteTally
            {
                public int Current;
                public int Total;
            }

            /// <summary>
            /// Walks out of OpenTK's binding stubs to the renderer frame that made the call.
            /// </summary>
            /// <remarks>
            /// File and line come from the embedded portable PDB the whole solution is built with, so this
            /// works from a Release binary. A frame with no declaring type is a runtime-generated marshalling
            /// stub and is skipped rather than reported.
            /// </remarks>
            private static CallSite Capture()
            {
                var trace = new StackTrace(skipFrames: 1, fNeedFileInfo: true);

                for (var i = 0; i < trace.FrameCount; i++)
                {
                    var frame = trace.GetFrame(i);
                    var method = frame?.GetMethod();
                    var type = method?.DeclaringType;

                    if (type is null || frame is null)
                    {
                        continue;
                    }

                    var ns = type.Namespace ?? string.Empty;

                    if (ns.StartsWith("OpenTK", StringComparison.Ordinal) || type == typeof(TrappedEntryPoint))
                    {
                        continue;
                    }

                    var file = frame.GetFileName();
                    var shortFile = file is null ? type.FullName ?? type.Name : ShortenPath(file);

                    return new CallSite(shortFile, frame.GetFileLineNumber(), $"{type.Name}.{method!.Name}");
                }

                return new CallSite("(unattributed)", 0, "(unknown)");
            }

            /// <summary>
            /// Trims an absolute source path down to a repository relative one.
            /// </summary>
            /// <remarks>
            /// The root is taken from this file's own compile-time path rather than matched by name.
            /// Matching by name is what an earlier version did, and it cannot work here: the renderer's
            /// sources live in <c>Renderer/Renderer/</c> while <c>Renderer/Renderer.cs</c> is a real file
            /// one level up, so any rule that collapses the repeated segment maps two different files onto
            /// one name -- and the repository itself sits under a path containing
            /// <c>ValveResourceFormat</c> twice.
            /// </remarks>
            private static string ShortenPath(string path)
            {
                var normalized = path.Replace('\\', '/');

                return normalized.StartsWith(RepositoryRoot, StringComparison.OrdinalIgnoreCase)
                    ? normalized[RepositoryRoot.Length..]
                    : normalized;
            }

            private static readonly string RepositoryRoot = FindRepositoryRoot();

            private static string FindRepositoryRoot([CallerFilePath] string thisFile = "")
            {
                const string Suffix = "Tests/Renderer/Golden/GLCallTrap.cs";

                var normalized = thisFile.Replace('\\', '/');

                return normalized.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
                    ? normalized[..^Suffix.Length]
                    : string.Empty;
            }
        }

        /// <summary>Where a trapped call came from.</summary>
        /// <param name="File">Source file, relative to the repository where it could be identified.</param>
        /// <param name="Line">Line within that file, or zero when the PDB carried none.</param>
        /// <param name="Member">Declaring type and method.</param>
        internal readonly record struct CallSite(string File, int Line, string Member)
        {
            /// <inheritdoc/>
            public override string ToString() => Line > 0 ? $"{File}:{Line} ({Member})" : $"{File} ({Member})";
        }

        /// <summary>
        /// The calls one source file made, kept per site so the report can name the line rather than only
        /// the file.
        /// </summary>
        private sealed class FileTally
        {
            private readonly Dictionary<CallSite, SiteDetail> Sites = [];
            private readonly HashSet<string> Names = new(StringComparer.Ordinal);

            /// <summary>Calls made from this file.</summary>
            public int Calls { get; private set; }

            /// <summary>Distinct call sites in this file.</summary>
            public int SiteCount => Sites.Count;

            /// <summary>Distinct GL entry points called from this file.</summary>
            public int EntryPointCount => Names.Count;

            public void Add(string entryPoint, int calls, CallSite site)
            {
                Calls += calls;
                Names.Add(entryPoint);

                if (!Sites.TryGetValue(site, out var detail))
                {
                    detail = new SiteDetail();
                    Sites[site] = detail;
                }

                detail.Calls += calls;
                detail.EntryPoints.Add(entryPoint);
            }

            /// <summary>The busiest call sites in this file, with what each of them calls.</summary>
            public IEnumerable<string> Describe()
            {
                const int Shown = 6;

                var ordered = Sites.OrderByDescending(static pair => pair.Value.Calls).ToList();

                foreach (var (site, detail) in ordered.Take(Shown))
                {
                    var entryPoints = string.Join(", ", detail.EntryPoints.Order(StringComparer.Ordinal).Take(5));
                    var more = detail.EntryPoints.Count > 5 ? $", +{detail.EntryPoints.Count - 5} more" : string.Empty;

                    yield return $"{site,-56} {detail.Calls,7:N0}  {entryPoints}{more}";
                }

                if (ordered.Count > Shown)
                {
                    var rest = ordered.Skip(Shown).Sum(static pair => pair.Value.Calls);

                    yield return $"{$"(+{ordered.Count - Shown} further sites)",-56} {rest,7:N0}";
                }
            }

            private sealed class SiteDetail
            {
                public int Calls;
                public HashSet<string> EntryPoints { get; } = new(StringComparer.Ordinal);
            }
        }

        /// <summary>
        /// Hands OpenTK a distinct recording thunk for every entry point it asks for.
        /// </summary>
        private sealed class TrapBindingsContext : IBindingsContext
        {
            /// <inheritdoc/>
            public nint GetProcAddress(string procName)
            {
                var entry = EntryPoints.GetOrAdd(procName, static name => new TrappedEntryPoint(name));

                TrapThunk thunk = entry.Note;

                lock (Thunks)
                {
                    Thunks.Add(thunk);
                }

                return Marshal.GetFunctionPointerForDelegate(thunk);
            }
        }
    }
}
