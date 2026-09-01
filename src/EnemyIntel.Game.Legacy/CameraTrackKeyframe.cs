namespace EnemyIntel.Game.Legacy;

public sealed record CameraTrackKeyframe(
    float RightX, float RightY, float RightZ,
    float UpX, float UpY, float UpZ,
    float ForwardX, float ForwardY, float ForwardZ,
    float PositionX, float PositionY, float PositionZ,
    float Fov,
    float? AnchorX = null,
    float? AnchorY = null,
    float? AnchorZ = null);

public sealed record CameraPointSettings(
    double TransitionSeconds,
    string Easing,
    double HoldSeconds,
    string MotionEffect = "None",
    double MotionAmount = 0.25,
    double MotionSpeed = 3.0,
    double MotionFade = 0.25,
    int MotionSeed = 1,
    double PathSmoothing = 0.75);

public sealed record CameraTrackPlaybackPoint(CameraTrackKeyframe Camera, CameraPointSettings Settings);

public sealed record CameraTrackDocument(
    int Capacity,
    Dictionary<int, CameraTrackKeyframe> Points,
    double TransitionSeconds = 3.0,
    bool Loop = false,
    string Easing = "Smooth",
    double HoldSeconds = 0.0,
    string Name = "Untitled Shot",
    Dictionary<int, CameraPointSettings>? PointSettings = null);
