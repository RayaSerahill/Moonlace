using System.Numerics;

namespace Moonlace.Rendering.Camera;

/// <summary>
/// Orbit camera: yaw/pitch around a target point at a distance.
/// Left-drag orbits, wheel zooms, middle/right-drag pans.
/// </summary>
public sealed class OrbitCamera
{
    private const float MinPitch = -1.55f;
    private const float MaxPitch = 1.55f;

    public Vector3 Target { get; set; }

    public float Yaw { get; set; }

    public float Pitch { get; set; } = 0.25f;

    public float Distance { get; set; } = 2f;

    public float MinDistance { get; set; } = 0.05f;

    public float MaxDistance { get; set; } = 100f;

    public Vector3 Position
    {
        get
        {
            var cp = MathF.Cos(Pitch);
            var offset = new Vector3(
                cp * MathF.Sin(Yaw),
                MathF.Sin(Pitch),
                cp * MathF.Cos(Yaw));
            return Target + offset * Distance;
        }
    }

    public void Orbit(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, MinPitch, MaxPitch);
    }

    public void Zoom(float wheelDelta)
    {
        Distance = Math.Clamp(Distance * MathF.Pow(0.88f, wheelDelta), MinDistance, MaxDistance);
    }

    public void Pan(float deltaX, float deltaY)
    {
        var view = ViewMatrix();
        // Camera right/up vectors from the view matrix rows.
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        var scale = Distance * 0.0016f;
        Target += (-right * deltaX + up * deltaY) * scale;
    }

    public Matrix4x4 ViewMatrix() => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);

    public Matrix4x4 ProjectionMatrix(float aspectRatio)
    {
        var near = Math.Max(0.01f, Distance * 0.01f);
        var far = Math.Max(50f, Distance * 50f);
        return Matrix4x4.CreatePerspectiveFieldOfView(0.8f, Math.Max(aspectRatio, 0.05f), near, far);
    }

    /// <summary>
    /// Positions the camera so a model with the given bounds is fully visible,
    /// looking at its center from a pleasant three-quarter angle.
    /// </summary>
    public void FrameBounds(Vector3 min, Vector3 max)
    {
        var center = (min + max) * 0.5f;
        var radius = Math.Max((max - min).Length() * 0.5f, 0.05f);

        Target = center;
        Yaw = 0.6f;
        Pitch = 0.2f;
        Distance = radius * 2.6f;
        MinDistance = radius * 0.2f;
        MaxDistance = radius * 20f;
    }
}
