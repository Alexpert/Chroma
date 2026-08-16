using System.Numerics;
using Chroma.Core.Compilation;
using Silk.NET.OpenGL;

namespace Chroma.Rendering;

/// <summary>
/// The camera and lights a wavefront frame needs, as plain values.
/// </summary>
/// <remarks>
/// Passed in rather than reached for, because <see cref="WavefrontRenderer"/> sets each stage
/// exactly the uniforms that stage reads. A driver strips a uniform a program does not use and
/// <see cref="Shader.SetUniform"/> throws on a name it cannot find — deliberately, so a typo is
/// loud — which means "set everything on everything" is not available here and would not be
/// wanted: which stage reads what is the clearest statement of what the split actually is.
/// </remarks>
public readonly record struct WavefrontFrame(
    Vector3 CameraPosition,
    Vector3 CameraForward,
    Vector3 CameraRight,
    Vector3 CameraUp,
    Vector2 InvResolution,
    int SampleIndex,
    int MaxBounces,
    int LightCount,
    int[] LightKind,
    Vector3[] LightVector,
    Vector3[] LightColor,
    float[] LightRadius);

/// <summary>
/// Traces a scene too large for one program, one stage of the path at a time.
/// </summary>
/// <remarks>
/// <para>
/// The megakernel holds a whole path in registers and a whole scene in one shader. Neither is
/// possible past a point: the driver caps a program, and a scene may hold more distinct geometry
/// than any one program can. So the geometry is split into chunks, each its own program, and the
/// path is split into stages, each its own dispatch over state in a buffer.
/// </para>
/// <para>
/// Per sample, with P chunks, B bounces and L lights:
/// </para>
/// <code>
/// spawn
/// for bounce in 0..B-1:
///     intersect x P                      -- primary wave, nearest-hit reduced across chunks
///     shade                              -- emission, medium, the light draws, the BRDF sample
///     for light in 0..L-1:
///         [ intersect x P; advance ] x S -- S is 1 opaque, MAX_SHADOW_STEPS through glass
///     connect
/// gather
/// </code>
/// <para>
/// One dispatch becomes tens, each covering every pixel whether or not its path is still alive,
/// and there is no compaction. Written before measuring, this comment said the result had to be
/// slower than the megakernel everywhere. <b>It is not.</b> On the two scenes heavy enough to be
/// <i>register</i> bound rather than instruction bound it is faster — <c>chess-full</c> by 1.44x,
/// <c>sweeps</c> by 1.17x — because splitting the path cuts what one kernel holds live, which is
/// the classic reason a wavefront helps. Light scenes pay the dispatch count and lose about 1.25x.
/// <para>
/// So the trade is dispatch count against occupancy, and which way it goes depends on whether a
/// scene was register bound to begin with — the same property that decides whether it meets C5041
/// rather than the instruction cap. Scenes that have to be split are on the gaining side by
/// construction. Compaction is what would win back the rest; see documents/performance.md.
/// </para>
/// <para>
/// Compute only. The OpenGL 3.3 fallback has neither storage buffers nor <c>imageStore</c>, and
/// storage buffers specifically: the scene tables must be at their binding points rather than
/// behind sampler uniforms, because a stage that carries no geometry does not read them and the
/// driver strips the samplers it would otherwise be handed.
/// </para>
/// </remarks>
public sealed class WavefrontRenderer : IDisposable
{
    /// <summary>Matches MAX_SHADOW_STEPS in raytrace.glsl. They change together or not at all.</summary>
    private const int MaxShadowSteps = 4;

    /// <summary>Matches the <c>local_size</c> declared in raytrace.glsl.</summary>
    private const int WorkgroupSize = 8;

    /// <summary>How often the driver is asked whether it has finished a program.</summary>
    /// <remarks>
    /// Short enough that a program already in the driver's cache is not held up noticeably, long
    /// enough that a two-minute compile is not spent asking. Nothing is waiting on the answer but
    /// the number on screen.
    /// </remarks>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private const int StageSpawn = 1;
    private const int StageIntersect = 2;
    private const int StageShade = 3;
    private const int StageAdvance = 4;
    private const int StageConnect = 5;
    private const int StageGather = 6;

    private const int WavePrimary = 0;
    private const int WaveShadow = 1;

    /// <summary>Storage-buffer bindings, continuing where the scene tables stop.</summary>
    private const uint PathsBinding = 5;
    private const uint HitsBinding = 6;
    private const uint ShadowBinding = 7;

    /// <summary>vec4s per record, matching the structs in raytrace.glsl.</summary>
    private const int PathVectors = 6;
    private const int ShadowVectors = 5;

    private readonly GL _gl;
    private readonly CompiledScene _scene;

    /// <summary>One program per chunk of geometry, serving both waves. See <c>uWave</c>.</summary>
    private readonly Shader[] _chunks;

    private readonly Shader _spawn;
    private readonly Shader _shade;
    private readonly Shader _advance;
    private readonly Shader _connect;
    private readonly Shader _gather;

    private uint _paths;
    private uint _hits;
    private uint _shadow;

    private int _width;
    private int _height;
    private int _lights;

    /// <param name="emitTo">
    /// Where <c>--emit-shader</c> should write, or null. One file per program, named for the stage,
    /// because a wavefront has no single "the shader" and a driver error naming a line still has to
    /// name a line in a file that exists.
    /// </param>
    /// <param name="progress">
    /// Stepped as each program comes back, or null to compile without saying so.
    /// </param>
    /// <remarks>
    /// <para>
    /// These programs are independent — none of them reads anything another produces — so there was
    /// never a reason for them to be compiled one after another beyond the shape of the loop that
    /// made them. Every stage is handed over, then every program linked, then every program asked
    /// about, which is why <see cref="PendingProgram"/> has three steps rather than one.
    /// </para>
    /// <para>
    /// Worth 3.1x on a cold start: a <c>palisade</c> forced to ten chunks compiles its fifteen
    /// programs in 3.6 s against 11.1 s one at a time. It is worth nothing on a warm one, where the
    /// driver returns each program from its own cache in a millisecond or two, and nothing at all
    /// on a scene with one program — which is every scene that is not chunked. See
    /// <see cref="GlCapabilities.ParallelCompile"/> for the part of this that is not obvious.
    /// </para>
    /// </remarks>
    public WavefrontRenderer(
        GL gl,
        CompiledScene scene,
        string shaderPath,
        IReadOnlyList<string> defines,
        string version,
        string? emitTo = null,
        StartupProgress? progress = null,
        bool canPoll = false)
    {
        _gl = gl;
        _scene = scene;

        PendingProgram Stage(int stage, string? geometry, string name)
        {
            string[] all = [.. defines, "CHROMA_WAVEFRONT 1", $"CHROMA_STAGE {stage}"];

            if (emitTo is not null)
            {
                string directory = Path.GetDirectoryName(emitTo) ?? ".";
                string stem = Path.GetFileNameWithoutExtension(emitTo);
                string extension = Path.GetExtension(emitTo) is { Length: > 0 } e ? e : ".glsl";

                File.WriteAllText(
                    Path.Combine(directory, $"{stem}.{name}{extension}"),
                    Shader.Assemble(shaderPath, all, geometry, version));
            }

            return PendingProgram.Compile(gl, shaderPath, all, geometry, version, $"the {name} program");
        }

        // The only programs that carry geometry. Every other stage is compiled once, from a file
        // with the marker left empty, and is the same handful of instructions whatever the scene
        // holds -- which is the property that makes the split worth anything.
        PendingProgram[] pending =
        [
            .. scene.Chunks.Select((chunk, i) => Stage(StageIntersect, chunk.Geometry, $"intersect{i}")),
            Stage(StageSpawn, null, "spawn"),
            Stage(StageShade, null, "shade"),
            Stage(StageAdvance, null, "advance"),
            Stage(StageConnect, null, "connect"),
            Stage(StageGather, null, "gather"),
        ];

        // Every stage is with the driver before the first link asks for one back. The other order
        // -- compile, link, compile, link -- is what a loop over Shader.Compute does, and it makes
        // each program wait for the one before it however many threads the driver has.
        foreach (PendingProgram program in pending)
        {
            program.Link();
        }

        Shader[] programs = Collect(pending, progress, canPoll);

        _chunks = [.. programs.Take(scene.Chunks.Count)];

        _spawn = programs[^5];
        _shade = programs[^4];
        _advance = programs[^3];
        _connect = programs[^2];
        _gather = programs[^1];
    }

    /// <summary>Waits for every program, counting them off as the driver finishes them.</summary>
    /// <remarks>
    /// <para>
    /// Completed in the order they were issued, which is the simple version and costs nothing: the
    /// ones behind the one being waited on are being compiled at the same time, so the total is
    /// the slowest program rather than the sum.
    /// </para>
    /// <para>
    /// <paramref name="canPoll"/> decides nothing about the compiling, only about what the count
    /// says while it happens: without <c>GL_ARB_parallel_shader_compile</c> there is no way to ask
    /// a program how it is getting on that does not wait for it, so the count moves as each program
    /// is collected rather than as each one finishes. It comes from the same capability that
    /// enabled the threads, which is why a machine without it gets both the slower compile and the
    /// coarser count.
    /// </para>
    /// </remarks>
    private static Shader[] Collect(PendingProgram[] pending, StartupProgress? progress, bool canPoll)
    {
        Shader[] programs = new Shader[pending.Length];
        bool poll = canPoll && progress is not null;

        for (int i = 0; i < pending.Length; i++)
        {
            progress?.Step(Label(pending, poll ? Ready(pending) : i));

            // Waiting by asking rather than by blocking, so the count can move while the driver
            // works. It ends exactly where Complete() below would have stopped waiting anyway --
            // a program that never reports itself finished would have blocked there just as long
            // -- so there is no deadline to get wrong.
            while (poll && !pending[i].IsReady())
            {
                Thread.Sleep(PollInterval);
                progress!.Step(Label(pending, Ready(pending)));
            }

            programs[i] = pending[i].Complete();
        }

        return programs;
    }

    private static int Ready(PendingProgram[] pending) => pending.Count(program => program.IsReady());

    private static string Label(PendingProgram[] pending, int done) =>
        $"compiling {pending.Length} programs, {done} back";

    /// <summary>How many programs the driver was asked to compile.</summary>
    public int ProgramCount => _chunks.Length + 5;

    /// <summary>What the ray-state buffers cost at the current size, in bytes.</summary>
    public long Bytes =>
        (long)_width * _height * ((PathVectors * 16) + 16 + ((long)ShadowVectors * 16 * Math.Max(_lights, 1)));

    /// <summary>
    /// One sample per pixel, folded into the accumulation image.
    /// </summary>
    /// <remarks>
    /// A barrier between every pass, and it is not optional: the whole scheme rests on the passes
    /// being sequential, which is also what makes the nearest-hit reduce free. Without it a chunk
    /// would read a hit another chunk has not finished writing, and the picture would be right
    /// most frames.
    /// </remarks>
    public void Trace(int width, int height, WavefrontFrame frame, SceneBuffers buffers, uint accumulation)
    {
        Resize(width, height, Math.Max(frame.LightCount, 1));

        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, PathsBinding, _paths);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, HitsBinding, _hits);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, ShadowBinding, _shadow);

        Begin(_spawn, buffers);
        _spawn.SetUniform("uSampleIndex", frame.SampleIndex);
        _spawn.SetUniform("uInvResolution", frame.InvResolution);
        _spawn.SetUniform("uCameraPosition", frame.CameraPosition);
        _spawn.SetUniform("uCameraForward", frame.CameraForward);
        _spawn.SetUniform("uCameraRight", frame.CameraRight);
        _spawn.SetUniform("uCameraUp", frame.CameraUp);
        Dispatch();

        for (int bounce = 0; bounce < frame.MaxBounces; bounce++)
        {
            Wave(WavePrimary, 0, buffers);

            Begin(_shade, buffers);
            Lights(_shade, frame);
            Dispatch();

            // One light at a time, so a shadow ray's boundaries need only one entry per pixel in
            // the hit buffer. The samples for every light were drawn together in shade, because
            // the seed advances in a fixed order and deferring a draw would change every random
            // number after it; only the tracing is spread out here.
            for (int light = 0; light < frame.LightCount; light++)
            {
                // An opaque scene answers in one pass and has nothing to advance: "is anything in
                // the way" composes across chunks with no ordering at all.
                int steps = _scene.HasTransmission ? MaxShadowSteps : 1;

                for (int step = 0; step < steps; step++)
                {
                    Wave(WaveShadow, light, buffers);

                    if (!_scene.HasTransmission)
                    {
                        continue;
                    }

                    Begin(_advance, buffers);
                    _advance.SetUniform("uLight", light);
                    Dispatch();
                }
            }

            Begin(_connect, buffers);
            _connect.SetUniform("uBounce", bounce);
            _connect.SetUniform("uLightCount", frame.LightCount);
            Dispatch();
        }

        _gl.BindImageTexture(0, accumulation, 0, false, 0, GLEnum.ReadWrite, GLEnum.Rgba32f);

        Begin(_gather, buffers);
        _gather.SetUniform("uSampleIndex", frame.SampleIndex);
        Dispatch();

        // The resolve pass samples what was just written as a texture, which is a different
        // access than the image store that wrote it.
        _gl.MemoryBarrier(MemoryBarrierMask.TextureFetchBarrierBit);
    }

    /// <summary>Every chunk in turn, each handed what the ones before it found.</summary>
    private void Wave(int wave, int light, SceneBuffers buffers)
    {
        for (int chunk = 0; chunk < _chunks.Length; chunk++)
        {
            Shader program = _chunks[chunk];

            Begin(program, buffers);
            program.SetUniform("uWave", wave);
            program.SetUniform("uLight", light);
            program.SetUniform("uFirstChunk", chunk == 0 ? 1 : 0);

            // Only a chunk that actually has placements declares this, and a chunk of a scene that
            // repeats something may still have none of its own.
            if (_scene.Chunks[chunk].NodeCount > 0)
            {
                program.SetUniform("uNodeCount", _scene.Chunks[chunk].NodeCount);
            }

            Dispatch();
        }
    }

    private void Begin(Shader program, SceneBuffers buffers)
    {
        program.Use();

        // After Use(), never before: glUniform* writes into whichever program is current.
        buffers.BindTo(program);
        program.SetUniform("uFrame", _width, _height);
    }

    private void Dispatch()
    {
        _gl.DispatchCompute(
            (uint)((_width + WorkgroupSize - 1) / WorkgroupSize),
            (uint)((_height + WorkgroupSize - 1) / WorkgroupSize),
            1);

        _gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
    }

    private static void Lights(Shader program, WavefrontFrame frame)
    {
        program.SetUniform("uLightCount", frame.LightCount);
        program.SetUniform("uLightKind", (ReadOnlySpan<int>)frame.LightKind, frame.LightCount);
        program.SetUniform("uLightVector", (ReadOnlySpan<Vector3>)frame.LightVector, frame.LightCount);
        program.SetUniform("uLightColor", (ReadOnlySpan<Vector3>)frame.LightColor, frame.LightCount);
        program.SetUniform("uLightRadius", (ReadOnlySpan<float>)frame.LightRadius, frame.LightCount);
    }

    private void Resize(int width, int height, int lights)
    {
        if (width == _width && height == _height && lights == _lights)
        {
            return;
        }

        Release();

        _width = width;
        _height = height;
        _lights = lights;

        long pixels = (long)width * height;

        _paths = Allocate(pixels * PathVectors * 16);
        _hits = Allocate(pixels * 16);
        _shadow = Allocate(pixels * lights * ShadowVectors * 16);
    }

    private uint Allocate(long bytes)
    {
        uint buffer = _gl.GenBuffer();

        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);

        unsafe
        {
            _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)bytes, null, BufferUsageARB.DynamicCopy);
        }

        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
        return buffer;
    }

    private void Release()
    {
        foreach (uint buffer in new[] { _paths, _hits, _shadow }.Where(handle => handle != 0))
        {
            _gl.DeleteBuffer(buffer);
        }

        _paths = 0;
        _hits = 0;
        _shadow = 0;
    }

    public void Dispose()
    {
        Release();

        foreach (Shader program in _chunks)
        {
            program.Dispose();
        }

        _spawn.Dispose();
        _shade.Dispose();
        _advance.Dispose();
        _connect.Dispose();
        _gather.Dispose();
    }
}
