using System.Numerics.Tensors;

namespace IndustrialDocsRag.Retrieval;

internal static class VectorMath
{
    /// <summary>
    /// 余弦相似度。走 <see cref="TensorPrimitives"/> 的 SIMD 实现，不手写循环。
    /// </summary>
    /// <remarks>
    /// 零向量会让余弦变成 0/0，这里统一返回 0。放任 NaN 流进排序会很难查：
    /// NaN 参与比较时所有关系运算都是 false，排序结果会变得依赖于底层算法的实现细节。
    /// </remarks>
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"向量维度不一致：{a.Length} vs {b.Length}");
        }

        if (a.Length == 0)
        {
            return 0d;
        }

        var score = TensorPrimitives.CosineSimilarity<float>(a, b);
        return float.IsNaN(score) ? 0d : score;
    }

    /// <summary>就地 L2 归一化。全零向量原样返回。</summary>
    public static void NormalizeInPlace(float[] vector)
    {
        var norm = TensorPrimitives.Norm<float>(vector);
        if (norm > 0f)
        {
            TensorPrimitives.Divide<float>(vector, norm, vector);
        }
    }
}
