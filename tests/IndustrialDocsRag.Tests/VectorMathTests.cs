using IndustrialDocsRag.Retrieval;

namespace IndustrialDocsRag.Tests;

public class VectorMathTests
{
    [Fact]
    public void Cosine_相同向量_返回1()
    {
        float[] a = [1f, 2f, 3f];
        float[] b = [1f, 2f, 3f];

        Assert.Equal(1d, VectorMath.Cosine(a, b), precision: 5);
    }

    [Fact]
    public void Cosine_正交向量_返回0()
    {
        float[] a = [1f, 0f];
        float[] b = [0f, 1f];

        Assert.Equal(0d, VectorMath.Cosine(a, b), precision: 5);
    }

    [Fact]
    public void Cosine_方向相反_返回负1()
    {
        float[] a = [1f, 1f];
        float[] b = [-1f, -1f];

        Assert.Equal(-1d, VectorMath.Cosine(a, b), precision: 5);
    }

    [Fact]
    public void Cosine_长度不同_抛异常()
    {
        float[] a = [1f, 2f];
        float[] b = [1f, 2f, 3f];

        Assert.Throws<ArgumentException>(() => VectorMath.Cosine(a, b));
    }

    /// <summary>
    /// 零向量会让余弦变成 0/0。放任 NaN 流进排序很难查：NaN 参与比较时所有关系运算都是 false，
    /// 排序结果会变得依赖底层算法的实现细节，表现为"偶尔顺序不对"。
    /// </summary>
    [Fact]
    public void Cosine_零向量_返回0而不是NaN()
    {
        float[] zero = [0f, 0f, 0f];
        float[] other = [1f, 2f, 3f];

        var score = VectorMath.Cosine(zero, other);

        Assert.False(double.IsNaN(score));
        Assert.Equal(0d, score);
    }

    [Fact]
    public void NormalizeInPlace_归一化后模长为1()
    {
        float[] v = [3f, 4f];

        VectorMath.NormalizeInPlace(v);

        Assert.Equal(0.6d, v[0], precision: 5);
        Assert.Equal(0.8d, v[1], precision: 5);
    }

    [Fact]
    public void NormalizeInPlace_全零向量_原样返回不抛异常()
    {
        float[] v = [0f, 0f];

        VectorMath.NormalizeInPlace(v);

        Assert.Equal([0f, 0f], v);
    }
}
