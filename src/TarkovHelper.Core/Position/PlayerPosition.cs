namespace TarkovHelper.Core.Position;

// Unity world-space coordinates parsed from a screenshot filename.
// Y is up (Unity convention); map projections typically plot X/Z and treat
// Y as elevation/floor. YawDegrees is derived from the rotation quaternion.
public readonly record struct PlayerPosition(float X, float Y, float Z, float YawDegrees, DateTime Timestamp);
