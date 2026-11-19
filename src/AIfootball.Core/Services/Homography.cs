namespace AIfootball.Core.Services;

/// <summary>
/// 四点透视单应变换：把图像坐标映射到球门平面坐标（米，射手视角）。
///
/// 单机位比赛视频的落点估计只能做到"把画面上的球位置反投影到门平面"，
/// 因此四个门角点的标定精度直接决定落点误差。用 4 点精确解（8 未知数 8 方程），
/// 不做最小二乘——多标的点反而会引入不可见的残差。
/// </summary>
public sealed class Homography
{
    private readonly double[] _h;

    private Homography(double[] h) => _h = h;

    /// <summary>
    /// 用四个图像点 + 对应四个世界点求解。点顺序必须一一对应。
    /// 四点接近共线时返回 null（无解或解不稳定）。
    /// </summary>
    public static Homography? Solve((double X, double Y)[] imagePoints, (double X, double Y)[] worldPoints)
    {
        if (imagePoints is null || worldPoints is null) return null;
        if (imagePoints.Length != 4 || worldPoints.Length != 4) return null;

        // 未知量顺序：h11 h12 h13 h21 h22 h23 h31 h32，h33 固定为 1
        var a = new double[8, 9];
        for (var i = 0; i < 4; i++)
        {
            var (u, v) = imagePoints[i];
            var (x, y) = worldPoints[i];

            var r1 = i * 2;
            a[r1, 0] = u; a[r1, 1] = v; a[r1, 2] = 1;
            a[r1, 6] = -u * x; a[r1, 7] = -v * x;
            a[r1, 8] = x;

            var r2 = r1 + 1;
            a[r2, 3] = u; a[r2, 4] = v; a[r2, 5] = 1;
            a[r2, 6] = -u * y; a[r2, 7] = -v * y;
            a[r2, 8] = y;
        }

        var solution = SolveLinear(a);
        if (solution is null) return null;

        // 反投影退化检查：四点在图像上几乎共线时行列式接近 0
        var area = Math.Abs(
            (imagePoints[1].X - imagePoints[0].X) * (imagePoints[3].Y - imagePoints[0].Y) -
            (imagePoints[3].X - imagePoints[0].X) * (imagePoints[1].Y - imagePoints[0].Y));
        if (area < 1.0) return null;

        return new Homography(solution);
    }

    /// <summary>图像坐标 → 门平面坐标（米）。退化输入返回 null。</summary>
    public (double X, double Y)? Map(double u, double v)
    {
        var denominator = _h[6] * u + _h[7] * v + 1.0;
        if (Math.Abs(denominator) < 1e-9) return null;

        var x = (_h[0] * u + _h[1] * v + _h[2]) / denominator;
        var y = (_h[3] * u + _h[4] * v + _h[5]) / denominator;
        return double.IsFinite(x) && double.IsFinite(y) ? (x, y) : null;
    }

    /// <summary>高斯消元（部分主元）。无解或奇异时返回 null。</summary>
    private static double[]? SolveLinear(double[,] matrix)
    {
        const int n = 8;
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++)
            {
                if (Math.Abs(matrix[row, col]) > Math.Abs(matrix[pivot, col])) pivot = row;
            }

            if (Math.Abs(matrix[pivot, col]) < 1e-12) return null;

            if (pivot != col)
            {
                for (var k = col; k <= n; k++)
                {
                    (matrix[col, k], matrix[pivot, k]) = (matrix[pivot, k], matrix[col, k]);
                }
            }

            for (var row = 0; row < n; row++)
            {
                if (row == col) continue;
                var factor = matrix[row, col] / matrix[col, col];
                if (Math.Abs(factor) < 1e-18) continue;
                for (var k = col; k <= n; k++) matrix[row, k] -= factor * matrix[col, k];
            }
        }

        var result = new double[n];
        for (var i = 0; i < n; i++)
        {
            result[i] = matrix[i, n] / matrix[i, i];
            if (!double.IsFinite(result[i])) return null;
        }

        return result;
    }
}
