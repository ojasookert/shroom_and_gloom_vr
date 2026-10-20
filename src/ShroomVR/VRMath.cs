using UnityEngine;
using Valve.VR;

namespace ShroomVR;

/// <summary>
/// OpenVR (right-handed, -Z forward) to Unity (left-handed, +Z forward) conversions, matching
/// SteamVR_Utils.RigidTransform. Pure math so nothing here round-trips through IL2CPP icalls.
/// </summary>
public static class VRMath
{
    public static void ToUnity(HmdMatrix34_t m, out Vector3 pos, out Quaternion rot)
    {
        pos = new Vector3(m.m3, m.m7, -m.m11);

        // Rotation part with the Z axis mirrored.
        float m00 = m.m0, m01 = m.m1, m02 = -m.m2;
        float m10 = m.m4, m11 = m.m5, m12 = -m.m6;
        float m20 = -m.m8, m21 = -m.m9, m22 = m.m10;
        rot = FromRotationMatrix(m00, m01, m02, m10, m11, m12, m20, m21, m22);
    }

    public static Matrix4x4 ToUnityMatrix(HmdMatrix34_t m)
    {
        ToUnity(m, out var pos, out var rot);
        return Matrix4x4.TRS(pos, rot, Vector3.one);
    }

    public static Vector3 ToUnityVector(HmdVector3_t v) => new(v.v0, v.v1, -v.v2);

    public static Vector3 ToUnityAngularVelocity(HmdVector3_t v) => new(-v.v0, -v.v1, v.v2);

    /// <summary>Row-major 3x3 rotation matrix to quaternion (Shepperd's method).</summary>
    public static Quaternion FromRotationMatrix(float m00, float m01, float m02,
                                                float m10, float m11, float m12,
                                                float m20, float m21, float m22)
    {
        float trace = m00 + m11 + m22;
        float x, y, z, w;
        if (trace > 0f)
        {
            float s = Mathf.Sqrt(trace + 1f) * 2f;
            w = 0.25f * s;
            x = (m21 - m12) / s;
            y = (m02 - m20) / s;
            z = (m10 - m01) / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
            w = (m21 - m12) / s;
            x = 0.25f * s;
            y = (m01 + m10) / s;
            z = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
            w = (m02 - m20) / s;
            x = (m01 + m10) / s;
            y = 0.25f * s;
            z = (m12 + m21) / s;
        }
        else
        {
            float s = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
            w = (m10 - m01) / s;
            x = (m02 + m20) / s;
            y = (m12 + m21) / s;
            z = 0.25f * s;
        }
        float n = Mathf.Sqrt(x * x + y * y + z * z + w * w);
        return n > 1e-6f ? new Quaternion(x / n, y / n, z / n, w / n) : Quaternion.identity;
    }

    /// <summary>
    /// OpenGL-style projection from IVRSystem.GetProjectionRaw tangents, passed exactly as OpenVR
    /// returns them. The x/y terms mirror OpenVR's own ComposeProjection (what GetProjectionMatrix
    /// returns, which SteamVR's Unity plugin uses unflipped); depth uses Unity's GL [-1,1] convention.
    /// Unity converts it for D3D itself (GL.GetGPUProjectionMatrix) when set on a camera.
    /// </summary>
    public static Matrix4x4 Projection(float left, float right, float top, float bottom, float near, float far)
    {
        float idx = 1f / (right - left);
        float idy = 1f / (bottom - top);
        var m = new Matrix4x4();
        m.m00 = 2f * idx;
        m.m02 = (right + left) * idx;
        m.m11 = 2f * idy;
        m.m12 = (bottom + top) * idy;
        m.m22 = -(far + near) / (far - near);
        m.m23 = -(2f * far * near) / (far - near);
        m.m32 = -1f;
        return m;
    }
}
