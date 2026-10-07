using System.Numerics;
using System.Runtime.InteropServices;

namespace ProjectTabletop.App.Projection.WaterGarden;

internal readonly record struct WaterFountainImpact(Vector2 Position, float NormalMomentum, float Mass, int Particles);

internal sealed record WaterFountainFluidDiagnostics(int ParticleCount, int Capacity, long Steps,
    double SimulatedSeconds, long EmittedParticles, long PoolImpactParticles, long EscapedParticles,
    long RockContacts, double MaximumSpeed, double MaximumDensity, int OccupiedVoxels,
    double AtlasMaximumDensity, int NonFiniteParticles, long TopEscapes, long SideEscapes,
    long RearEscapes, long FrontEscapes, long ExpiredParticles, long SourceCapacitySuppressedParticles);

/// <summary>
/// Bounded three-dimensional particle water. Gravity, symmetric neighbor pressure,
/// viscosity and cohesion move persistent particles; the actual stone surface supplies
/// collision constraints. The density volume is reconstructed from that state, never
/// from clock-driven stream shapes. Coordinates inside the solver use fountain units;
/// its public positions and velocities use the garden's world coordinates.
/// </summary>
internal sealed class WaterFountainFluid
{
    public const int GridX = 80, GridY = 48, GridZ = 64, AtlasTiles = 8;
    public const int AtlasWidth = GridX * AtlasTiles, AtlasHeight = GridY * AtlasTiles;
    public const int MaximumParticles = 2400;
    public const double FixedStep = 1.0 / 120.0;

    private const float ParticleRadius = .0030f;
    private const float RestSpacing = .0056f;
    private const float KernelRadius = .0120f;
    private const float KernelRadiusSquared = KernelRadius * KernelRadius;
    private const float ReconstructionRadius = .0105f;
    private const float ParticleVolume = RestSpacing * RestSpacing * RestSpacing;
    private const float KernelIntegral = .63829184f; // Integral of (1-r^2)^3 over the unit sphere.
    private const float DensityWeight = ParticleVolume / (KernelIntegral * KernelRadius * KernelRadius * KernelRadius);
    private const float ReconstructionWeight = ParticleVolume /
        (KernelIntegral * ReconstructionRadius * ReconstructionRadius * ReconstructionRadius);
    private const float SourceParticlesPerSecond = 2000;
    private const int ImpactColumns = 12, ImpactRows = 8;

    private static readonly Vector3 LocalMinimum = new(-.21f, -.04f, -.060f);
    private static readonly Vector3 LocalMaximum = new(.21f, .34f, .215f);
    private static readonly Vector3 LocalSize = LocalMaximum - LocalMinimum;
    private static readonly Vector3 GridCell = LocalSize / new Vector3(GridX, GridY, GridZ);
    private readonly float _scale;
    private readonly float _aspect;
    private readonly Vector3[] _positions = new Vector3[MaximumParticles];
    private readonly Vector3[] _velocities = new Vector3[MaximumParticles];
    private readonly Vector3[] _velocityChanges = new Vector3[MaximumParticles];
    private readonly float[] _density = new float[MaximumParticles];
    private readonly float[] _age = new float[MaximumParticles];
    private readonly int[] _next = new int[MaximumParticles];
    private readonly int[] _cellX = new int[MaximumParticles];
    private readonly int[] _cellY = new int[MaximumParticles];
    private readonly int[] _cellZ = new int[MaximumParticles];
    private readonly int _hashX = (int)MathF.Ceiling(LocalSize.X / KernelRadius);
    private readonly int _hashY = (int)MathF.Ceiling(LocalSize.Y / KernelRadius);
    private readonly int _hashZ = (int)MathF.Ceiling(LocalSize.Z / KernelRadius);
    private readonly int[] _heads;
    private readonly float[] _stoneDistance = new float[GridX * GridY * GridZ];
    private readonly Vector3[] _stoneNormal = new Vector3[GridX * GridY * GridZ];
    private readonly byte[] _atlas = new byte[AtlasWidth * AtlasHeight * 16];
    private readonly byte[] _volume = new byte[GridX * GridY * GridZ * 16];
    private readonly Vector2[] _impactPosition = new Vector2[ImpactColumns * ImpactRows];
    private readonly float[] _impactMomentum = new float[ImpactColumns * ImpactRows];
    private readonly int[] _impactCount = new int[ImpactColumns * ImpactRows];
    private int _count;
    private uint _random;
    private double _accumulator, _seconds, _sourceAccumulator;
    private long _steps, _emitted, _impacted, _escaped, _rockContacts;
    private long _topEscapes, _sideEscapes, _rearEscapes, _frontEscapes, _expired, _capacityDeferrals;
    private long _atlasRevision = -1;
    private long _volumeRevision = -1;
    private double _maximumSpeed, _maximumDensity, _atlasMaximumDensity;
    private int _occupiedVoxels, _nonFiniteParticles;

    public WaterFountainFluid(float aspect)
    {
        if (!float.IsFinite(aspect) || aspect is < .1f or > 10)
            throw new ArgumentOutOfRangeException(nameof(aspect));
        _aspect = aspect;
        _scale = Math.Min(aspect, 1);
        _heads = new int[_hashX * _hashY * _hashZ];
        BuildCollisionField();
        Reset();
    }

    public Vector3 WorldMinimum => ToWorld(LocalMinimum);
    public Vector3 WorldMaximum => ToWorld(LocalMaximum);
    public Vector3 WorldSize => LocalSize * _scale;
    public long Revision { get; private set; }
    public int ParticleCount => _count;
    public double SimulatedSeconds => _seconds;
    public ReadOnlySpan<float> DensityAtlas => MemoryMarshal.Cast<byte, float>(_atlas.AsSpan());

    public WaterFountainFluidDiagnostics GetDiagnostics() => new(_count, MaximumParticles, _steps,
        _seconds, _emitted, _impacted, _escaped, _rockContacts, _maximumSpeed * _scale,
        _maximumDensity, _occupiedVoxels, _atlasMaximumDensity, _nonFiniteParticles,
        _topEscapes, _sideEscapes, _rearEscapes, _frontEscapes, _expired, _capacityDeferrals);

    public void Reset()
    {
        _count = 0;
        _random = 0x7C43D2A9u;
        _accumulator = _seconds = _sourceAccumulator = 0;
        _steps = _emitted = _impacted = _escaped = _rockContacts = 0;
        _topEscapes = _sideEscapes = _rearEscapes = _frontEscapes = _expired = _capacityDeferrals = 0;
        _maximumSpeed = _maximumDensity = _atlasMaximumDensity = 0;
        _occupiedVoxels = _nonFiniteParticles = 0;
        Array.Clear(_impactPosition);
        Array.Clear(_impactMomentum);
        Array.Clear(_impactCount);
        Array.Clear(_atlas);
        Array.Clear(_volume);
        Revision++;
        _atlasRevision = Revision;
        _volumeRevision = Revision;
    }

    /// <summary>
    /// Advances only fixed physical steps. The source stays dry for the first second
    /// and ramps during the next second, matching Calm Water's established timing.
    /// Disabling the source allows existing airborne/attached water to drain naturally.
    /// The enclosing garden owns catch-up limits, so this method does not discard time.
    /// </summary>
    public void Advance(double seconds, bool sourceEnabled = true)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        _accumulator += seconds;
        while (_accumulator + 1e-10 >= FixedStep)
        {
            bool hadParticles = _count > 0;
            float flow = sourceEnabled ? WaterFountainLayout.FlowAmount(_seconds) : 0;
            Step((float)FixedStep, flow);
            _accumulator = Math.Max(0, _accumulator - FixedStep);
            _seconds += FixedStep;
            _steps++;
            if (hadParticles || _count > 0) Revision++;
        }
    }

    /// <summary>
    /// Returns the actual accumulated landing flux, grouped only by small spatial bins.
    /// Position is a garden surface UV; momentum is incoming vertical momentum. Reading
    /// consumes the flux, so a repeated draw cannot replay an old landing into the pond.
    /// </summary>
    public IReadOnlyList<WaterFountainImpact> DrainImpacts()
    {
        List<WaterFountainImpact>? result = null;
        for (int i = 0; i < _impactCount.Length; i++)
        {
            int count = _impactCount[i];
            if (count == 0) continue;
            result ??= [];
            result.Add(new(_impactPosition[i] / count, _impactMomentum[i],
                count * ParticleVolume * _scale * _scale * _scale, count));
            _impactPosition[i] = Vector2.Zero;
            _impactMomentum[i] = 0;
            _impactCount[i] = 0;
        }
        return result is null ? Array.Empty<WaterFountainImpact>() : result;
    }

    /// <summary>
    /// Reconstructs RGBA32F density/vx/vy/vz at voxel centers. Z slices occupy an 8 by 8
    /// tile atlas; tile dimensions are GridX by GridY. The returned byte array is reused
    /// and can be uploaded directly to an alpha-ignored floating-point CanvasBitmap.
    /// </summary>
    public byte[] BuildDensityAtlas()
    {
        if (_atlasRevision == Revision) return _atlas;
        Array.Clear(_atlas);
        Span<float> values = MemoryMarshal.Cast<byte, float>(_atlas.AsSpan());
        const float radius2 = ReconstructionRadius * ReconstructionRadius;
        for (int particle = 0; particle < _count; particle++)
        {
            Vector3 center = _positions[particle];
            Vector3 grid = (center - LocalMinimum) / GridCell - new Vector3(.5f);
            int left = Math.Max(0, (int)MathF.Ceiling(grid.X - ReconstructionRadius / GridCell.X));
            int right = Math.Min(GridX - 1, (int)MathF.Floor(grid.X + ReconstructionRadius / GridCell.X));
            int back = Math.Max(0, (int)MathF.Ceiling(grid.Y - ReconstructionRadius / GridCell.Y));
            int front = Math.Min(GridY - 1, (int)MathF.Floor(grid.Y + ReconstructionRadius / GridCell.Y));
            int bottom = Math.Max(0, (int)MathF.Ceiling(grid.Z - ReconstructionRadius / GridCell.Z));
            int top = Math.Min(GridZ - 1, (int)MathF.Floor(grid.Z + ReconstructionRadius / GridCell.Z));
            Vector3 velocity = _velocities[particle] * _scale;
            for (int z = bottom; z <= top; z++)
            for (int y = back; y <= front; y++)
            for (int x = left; x <= right; x++)
            {
                Vector3 offset = LocalMinimum + (new Vector3(x, y, z) + new Vector3(.5f)) * GridCell - center;
                float q = 1 - offset.LengthSquared() / radius2;
                if (q <= 0) continue;
                // A submerged reconstruction kernel cannot put optical water inside
                // the rock. Actual particles are kept outside by the collision solver.
                if (_stoneDistance[VolumeIndex(x, y, z)] < -.0002f) continue;
                float density = ReconstructionWeight * q * q * q;
                int pixel = AtlasIndex(x, y, z);
                values[pixel] += density;
                values[pixel + 1] += density * velocity.X;
                values[pixel + 2] += density * velocity.Y;
                values[pixel + 3] += density * velocity.Z;
            }
        }
        _occupiedVoxels = 0;
        _atlasMaximumDensity = 0;
        for (int pixel = 0; pixel < values.Length; pixel += 4)
        {
            float density = values[pixel];
            if (density <= 0) continue;
            _atlasMaximumDensity = Math.Max(_atlasMaximumDensity, density);
            if (density >= .20f) _occupiedVoxels++;
            values[pixel + 1] /= density;
            values[pixel + 2] /= density;
            values[pixel + 3] /= density;
        }
        _atlasRevision = Revision;
        return _atlas;
    }

    /// <summary>
    /// Returns the same RGBA32F samples in contiguous XYZ order, with X varying
    /// fastest, for a three-dimensional D2D resource texture. The row and slice
    /// strides are GridX * 16 and GridX * GridY * 16 bytes. Storage is retained;
    /// repeated reads of an unchanged revision perform neither reconstruction nor
    /// copying, and preserve the atlas diagnostics and verification entry point.
    /// </summary>
    public byte[] BuildDensityVolume()
    {
        if (_volumeRevision == Revision) return _volume;
        BuildDensityAtlas();
        const int rowBytes = GridX * 16;
        for (int z = 0; z < GridZ; z++)
        for (int y = 0; y < GridY; y++)
        {
            int source = ((y + z / AtlasTiles * GridY) * AtlasWidth + z % AtlasTiles * GridX) * 16;
            int destination = (z * GridY + y) * rowBytes;
            Buffer.BlockCopy(_atlas, source, _volume, destination, rowBytes);
        }
        _volumeRevision = Revision;
        return _volume;
    }

    private void Step(float dt, float sourceFlow)
    {
        Emit(dt, sourceFlow);
        if (_count == 0) return;
        BuildNeighborGrid();
        Array.Clear(_velocityChanges, 0, _count);
        for (int i = 0; i < _count; i++)
        {
            float density = DensityWeight;
            ForNeighborDensity(i, ref density);
            _density[i] = density;
            _maximumDensity = Math.Max(_maximumDensity, density);
        }
        for (int i = 0; i < _count; i++) ApplyNeighborForces(i, dt);
        for (int i = 0; i < _count; i++)
        {
            Vector3 velocity = _velocities[i] + _velocityChanges[i] + new Vector3(0, 0, -WaterFountainLayout.Gravity * dt);
            float speed2 = velocity.LengthSquared();
            if (speed2 > 1.44f) velocity *= 1.2f / MathF.Sqrt(speed2);
            Vector3 previous = _positions[i];
            Vector3 position = previous + velocity * dt;
            _age[i] += dt;
            if (!IsFinite(position) || !IsFinite(velocity))
            {
                _nonFiniteParticles++;
                RemoveAt(i--);
                continue;
            }
            // Collide before classifying a pool crossing: the bottom rock continues
            // beneath the pond plane and must not create fictitious impacts inside it.
            if (SampleStoneDistance(position) < ParticleRadius + .0015f)
                ResolveStone(ref position, ref velocity, dt);
            if (position.Z <= 0 && velocity.Z < 0)
            {
                float crossing = Math.Clamp(previous.Z / Math.Max(.000001f, previous.Z - position.Z), 0, 1);
                RecordImpact(Vector3.Lerp(previous, position, crossing), -velocity.Z);
                RemoveAt(i--);
                continue;
            }
            if (position.X < LocalMinimum.X + ParticleRadius || position.X > LocalMaximum.X - ParticleRadius ||
                position.Y < LocalMinimum.Y + ParticleRadius || position.Y > LocalMaximum.Y - ParticleRadius ||
                position.Z > LocalMaximum.Z - ParticleRadius || _age[i] > 12)
            {
                _escaped++;
                if (position.Z > LocalMaximum.Z - ParticleRadius) _topEscapes++;
                else if (position.X < LocalMinimum.X + ParticleRadius || position.X > LocalMaximum.X - ParticleRadius) _sideEscapes++;
                else if (position.Y < LocalMinimum.Y + ParticleRadius) _rearEscapes++;
                else if (position.Y > LocalMaximum.Y - ParticleRadius) _frontEscapes++;
                else _expired++;
                RemoveAt(i--);
                continue;
            }
            _positions[i] = position;
            _velocities[i] = velocity;
            _maximumSpeed = Math.Max(_maximumSpeed, velocity.Length());
        }
    }

    private void Emit(float dt, float flow)
    {
        if (flow <= 0) { _sourceAccumulator = 0; return; }
        _sourceAccumulator += SourceParticlesPerSecond * flow * dt;
        int requested = (int)_sourceAccumulator;
        _sourceAccumulator -= requested;
        int available = Math.Min(requested, MaximumParticles - _count);
        _capacityDeferrals += requested - available;
        for (int n = 0; n < available; n++)
        {
            int i = _count++;
            // Only the source is prescribed. Each parcel falls, spreads and leaves
            // ledges according to forces and collisions after this initialization.
            _positions[i] = new(-.025f + (RandomUnit() - .5f) * .130f,
                .053f + RandomUnit() * .012f, .1885f - RandomUnit() * .0015f);
            _velocities[i] = new((RandomUnit() - .5f) * .024f,
                .240f + (RandomUnit() - .5f) * .022f, -.012f);
            _age[i] = 0;
            _emitted++;
        }
    }

    private void BuildNeighborGrid()
    {
        Array.Fill(_heads, -1);
        for (int i = 0; i < _count; i++)
        {
            Vector3 cell = (_positions[i] - LocalMinimum) / KernelRadius;
            int x = _cellX[i] = Math.Clamp((int)cell.X, 0, _hashX - 1);
            int y = _cellY[i] = Math.Clamp((int)cell.Y, 0, _hashY - 1);
            int z = _cellZ[i] = Math.Clamp((int)cell.Z, 0, _hashZ - 1);
            int index = (z * _hashY + y) * _hashX + x;
            _next[i] = _heads[index];
            _heads[index] = i;
        }
    }

    private void ForNeighborDensity(int i, ref float density)
    {
        Vector3 position = _positions[i];
        for (int z = Math.Max(0, _cellZ[i] - 1); z <= Math.Min(_hashZ - 1, _cellZ[i] + 1); z++)
        for (int y = Math.Max(0, _cellY[i] - 1); y <= Math.Min(_hashY - 1, _cellY[i] + 1); y++)
        for (int x = Math.Max(0, _cellX[i] - 1); x <= Math.Min(_hashX - 1, _cellX[i] + 1); x++)
        for (int j = _heads[(z * _hashY + y) * _hashX + x]; j >= 0; j = _next[j])
        {
            if (i == j) continue;
            float q = 1 - Vector3.DistanceSquared(position, _positions[j]) / KernelRadiusSquared;
            if (q > 0) density += DensityWeight * q * q * q;
        }
    }

    private void ApplyNeighborForces(int i, float dt)
    {
        Vector3 position = _positions[i];
        float densityI = Math.Max(.35f, _density[i]);
        float pressureI = Math.Max(0, _density[i] - 1) / (densityI * densityI);
        for (int z = Math.Max(0, _cellZ[i] - 1); z <= Math.Min(_hashZ - 1, _cellZ[i] + 1); z++)
        for (int y = Math.Max(0, _cellY[i] - 1); y <= Math.Min(_hashY - 1, _cellY[i] + 1); y++)
        for (int x = Math.Max(0, _cellX[i] - 1); x <= Math.Min(_hashX - 1, _cellX[i] + 1); x++)
        for (int j = _heads[(z * _hashY + y) * _hashX + x]; j >= 0; j = _next[j])
        {
            if (j <= i) continue;
            Vector3 delta = position - _positions[j];
            float distance2 = delta.LengthSquared();
            if (distance2 >= KernelRadiusSquared || distance2 < 1e-12f) continue;
            float distance = MathF.Sqrt(distance2), q = 1 - distance2 / KernelRadiusSquared;
            float densityJ = Math.Max(.35f, _density[j]);
            float pressureJ = Math.Max(0, _density[j] - 1) / (densityJ * densityJ);
            // Symmetric pressure exchanges momentum between the two parcels. A short
            // repulsive core protects sparse streams where density alone undercounts.
            float pressure = .065f * (pressureI + pressureJ) *
                6 * DensityWeight * q * q / KernelRadiusSquared;
            float core = Math.Max(0, .0041f - distance) * 3600 / distance;
            // Weak cohesion retains clear rivulets; viscosity damps only relative
            // velocity, preserving the stream's overall forward momentum.
            float cohesion = .055f * q / Math.Max(.002f, distance);
            Vector3 change = delta * ((pressure + core - cohesion) * dt) +
                (_velocities[j] - _velocities[i]) * (q * q * 1.10f * dt);
            float change2 = change.LengthSquared();
            if (change2 > .0016f) change *= .04f / MathF.Sqrt(change2);
            _velocityChanges[i] += change;
            _velocityChanges[j] -= change;
        }
    }

    private void ResolveStone(ref Vector3 position, ref Vector3 velocity, float dt)
    {
        for (int iteration = 0; iteration < 2; iteration++)
        {
            float distance = ExactStoneDistance(position);
            if (distance >= ParticleRadius) return;
            Vector3 normal = SampleStoneNormal(position);
            if (normal.LengthSquared() < .25f) normal = Vector3.UnitZ;
            position += normal * Math.Min(.012f, ParticleRadius - distance + .00003f);
            float inward = Vector3.Dot(velocity, normal);
            if (inward < 0) velocity -= normal * inward; // Inelastic wet contact, with free tangential flow.
            velocity *= MathF.Exp(-.24f * dt);
            _rockContacts++;
        }
    }

    private void RecordImpact(Vector3 local, float speed)
    {
        Vector3 world = ToWorld(local);
        Vector2 uv = new(.5f + world.X / _aspect, .5f + world.Y);
        int x = Math.Clamp((int)((local.X - LocalMinimum.X) / LocalSize.X * ImpactColumns), 0, ImpactColumns - 1);
        int y = Math.Clamp((int)((local.Y - LocalMinimum.Y) / LocalSize.Y * ImpactRows), 0, ImpactRows - 1);
        int bucket = y * ImpactColumns + x;
        _impactPosition[bucket] += uv;
        _impactMomentum[bucket] += ParticleVolume * _scale * _scale * _scale * speed * _scale;
        _impactCount[bucket]++;
        _impacted++;
    }

    private void RemoveAt(int index)
    {
        int last = --_count;
        if (index == last) return;
        _positions[index] = _positions[last];
        _velocities[index] = _velocities[last];
        _velocityChanges[index] = _velocityChanges[last];
        _density[index] = _density[last];
        _age[index] = _age[last];
    }

    private void BuildCollisionField()
    {
        for (int z = 0; z < GridZ; z++)
        for (int y = 0; y < GridY; y++)
        for (int x = 0; x < GridX; x++)
            _stoneDistance[VolumeIndex(x, y, z)] = ExactStoneDistance(
                LocalMinimum + (new Vector3(x, y, z) + new Vector3(.5f)) * GridCell);
        for (int z = 0; z < GridZ; z++)
        for (int y = 0; y < GridY; y++)
        for (int x = 0; x < GridX; x++)
        {
            float Sample(int a, int b, int c) => _stoneDistance[VolumeIndex(
                Math.Clamp(a, 0, GridX - 1), Math.Clamp(b, 0, GridY - 1), Math.Clamp(c, 0, GridZ - 1))];
            Vector3 gradient = new((Sample(x + 1, y, z) - Sample(x - 1, y, z)) / GridCell.X,
                (Sample(x, y + 1, z) - Sample(x, y - 1, z)) / GridCell.Y,
                (Sample(x, y, z + 1) - Sample(x, y, z - 1)) / GridCell.Z);
            _stoneNormal[VolumeIndex(x, y, z)] = gradient.LengthSquared() > 1e-8f
                ? Vector3.Normalize(gradient) : Vector3.UnitZ;
        }
    }

    private float SampleStoneDistance(Vector3 point)
    {
        Coordinates(point, out int x, out int y, out int z, out Vector3 t);
        int a = VolumeIndex(x, y, z), b = a + GridX * GridY;
        float back = Lerp(Lerp(_stoneDistance[a], _stoneDistance[a + 1], t.X),
            Lerp(_stoneDistance[a + GridX], _stoneDistance[a + GridX + 1], t.X), t.Y);
        float front = Lerp(Lerp(_stoneDistance[b], _stoneDistance[b + 1], t.X),
            Lerp(_stoneDistance[b + GridX], _stoneDistance[b + GridX + 1], t.X), t.Y);
        return Lerp(back, front, t.Z);
    }

    private Vector3 SampleStoneNormal(Vector3 point)
    {
        Coordinates(point, out int x, out int y, out int z, out Vector3 t);
        int a = VolumeIndex(x, y, z), b = a + GridX * GridY;
        Vector3 back = Vector3.Lerp(Vector3.Lerp(_stoneNormal[a], _stoneNormal[a + 1], t.X),
            Vector3.Lerp(_stoneNormal[a + GridX], _stoneNormal[a + GridX + 1], t.X), t.Y);
        Vector3 front = Vector3.Lerp(Vector3.Lerp(_stoneNormal[b], _stoneNormal[b + 1], t.X),
            Vector3.Lerp(_stoneNormal[b + GridX], _stoneNormal[b + GridX + 1], t.X), t.Y);
        Vector3 normal = Vector3.Lerp(back, front, t.Z);
        return normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitZ;
    }

    private static void Coordinates(Vector3 point, out int x, out int y, out int z, out Vector3 t)
    {
        Vector3 grid = Vector3.Clamp((point - LocalMinimum) / GridCell - new Vector3(.5f),
            Vector3.Zero, new Vector3(GridX - 1.0001f, GridY - 1.0001f, GridZ - 1.0001f));
        x = (int)grid.X; y = (int)grid.Y; z = (int)grid.Z;
        t = grid - new Vector3(x, y, z);
    }

    private static float ExactStoneDistance(Vector3 local) =>
        WaterFountainLayout.StoneDistance(local + new Vector3(0, WaterFountainLayout.AnchorY, 0), 1);

    private Vector3 ToWorld(Vector3 local) => local * _scale + new Vector3(0, WaterFountainLayout.AnchorY, 0);
    private static int VolumeIndex(int x, int y, int z) => (z * GridY + y) * GridX + x;
    private static int AtlasIndex(int x, int y, int z) =>
        ((y + z / AtlasTiles * GridY) * AtlasWidth + x + z % AtlasTiles * GridX) * 4;
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private float RandomUnit()
    {
        _random ^= _random << 13;
        _random ^= _random >> 17;
        _random ^= _random << 5;
        return (_random >> 8) * (1f / 16777216);
    }
}
