using Valve.VR;

namespace EveDeck.VrSpike;

// Rigid-transform helpers for HmdMatrix34_t.
//
// The matrix is row-major 3x4, an implicit 4th row of (0,0,0,1):
//   [ m0  m1  m2  | m3  ]
//   [ m4  m5  m6  | m7  ]
//   [ m8  m9  m10 | m11 ]
// so translation is (m3, m7, m11) and the rotation is the left 3x3.
internal static class VrMath
{
    public static HmdMatrix34_t Identity => new()
    {
        m0 = 1, m1 = 0, m2 = 0, m3 = 0,
        m4 = 0, m5 = 1, m6 = 0, m7 = 0,
        m8 = 0, m9 = 0, m10 = 1, m11 = 0,
    };

    public static (float X, float Y, float Z) Position(in HmdMatrix34_t m) => (m.m3, m.m7, m.m11);

    // OpenVR convention: a device looks down its own -Z.
    public static HmdVector3_t Forward(in HmdMatrix34_t m) => new() { v0 = -m.m2, v1 = -m.m6, v2 = -m.m10 };

    public static HmdVector3_t Origin(in HmdMatrix34_t m) => new() { v0 = m.m3, v1 = m.m7, v2 = m.m11 };

    public static HmdVector3_t Normalize(HmdVector3_t v)
    {
        var len = MathF.Sqrt((v.v0 * v.v0) + (v.v1 * v.v1) + (v.v2 * v.v2));
        if (len <= 1e-6f) return new HmdVector3_t { v0 = 0, v1 = 0, v2 = -1 };
        return new HmdVector3_t { v0 = v.v0 / len, v1 = v.v1 / len, v2 = v.v2 / len };
    }

    public static HmdVector3_t Cross(HmdVector3_t a, HmdVector3_t b) => new()
    {
        v0 = (a.v1 * b.v2) - (a.v2 * b.v1),
        v1 = (a.v2 * b.v0) - (a.v0 * b.v2),
        v2 = (a.v0 * b.v1) - (a.v1 * b.v0),
    };

    // A quad at `point` turned so its +Z (the face a viewer sees) points at `viewer`.
    public static HmdMatrix34_t FaceToward(HmdVector3_t point, HmdVector3_t viewer)
    {
        var z = Normalize(new HmdVector3_t { v0 = viewer.v0 - point.v0, v1 = viewer.v1 - point.v1, v2 = viewer.v2 - point.v2 });
        var up = new HmdVector3_t { v0 = 0, v1 = 1, v2 = 0 };

        // Looking straight up or down leaves the cross product degenerate; fall back to world -Z.
        if (MathF.Abs(z.v1) > 0.99f) up = new HmdVector3_t { v0 = 0, v1 = 0, v2 = -1 };

        var x = Normalize(Cross(up, z));
        var y = Cross(z, x);

        return new HmdMatrix34_t
        {
            m0 = x.v0, m1 = y.v0, m2 = z.v0, m3 = point.v0,
            m4 = x.v1, m5 = y.v1, m6 = z.v1, m7 = point.v1,
            m8 = x.v2, m9 = y.v2, m10 = z.v2, m11 = point.v2,
        };
    }

    public static HmdMatrix34_t Multiply(in HmdMatrix34_t a, in HmdMatrix34_t b)
    {
        Span<float> A = [a.m0, a.m1, a.m2, a.m3, a.m4, a.m5, a.m6, a.m7, a.m8, a.m9, a.m10, a.m11];
        Span<float> B = [b.m0, b.m1, b.m2, b.m3, b.m4, b.m5, b.m6, b.m7, b.m8, b.m9, b.m10, b.m11];
        Span<float> C = stackalloc float[12];

        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                var sum = 0f;
                for (var k = 0; k < 3; k++) sum += A[(row * 4) + k] * B[(k * 4) + col];
                C[(row * 4) + col] = sum;
            }

            // Translation column also picks up this row's own translation.
            var t = A[(row * 4) + 3];
            for (var k = 0; k < 3; k++) t += A[(row * 4) + k] * B[(k * 4) + 3];
            C[(row * 4) + 3] = t;
        }

        return new HmdMatrix34_t
        {
            m0 = C[0], m1 = C[1], m2 = C[2], m3 = C[3],
            m4 = C[4], m5 = C[5], m6 = C[6], m7 = C[7],
            m8 = C[8], m9 = C[9], m10 = C[10], m11 = C[11],
        };
    }

    // Inverse of a rigid transform: transpose the rotation, then re-express the translation.
    // Cheaper and steadier than a general inverse, and these matrices are always rigid.
    public static HmdMatrix34_t Invert(in HmdMatrix34_t m)
    {
        float r00 = m.m0, r01 = m.m1, r02 = m.m2;
        float r10 = m.m4, r11 = m.m5, r12 = m.m6;
        float r20 = m.m8, r21 = m.m9, r22 = m.m10;
        float tx = m.m3, ty = m.m7, tz = m.m11;

        return new HmdMatrix34_t
        {
            m0 = r00, m1 = r10, m2 = r20, m3 = -((r00 * tx) + (r10 * ty) + (r20 * tz)),
            m4 = r01, m5 = r11, m6 = r21, m7 = -((r01 * tx) + (r11 * ty) + (r21 * tz)),
            m8 = r02, m9 = r12, m10 = r22, m11 = -((r02 * tx) + (r12 * ty) + (r22 * tz)),
        };
    }
}
