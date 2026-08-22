using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Retrieval.Sparse;
using Microsoft.Extensions.DependencyInjection;

namespace IndustrialDocsRag.App.Commands;

/// <summary>
/// 查询前把落盘的 BM25 模型装回编码器。
/// <para>
/// 查询侧必须用入库时那份 df / N / avgdl。拿单条查询重新 Fit 得到的是垃圾权重，
/// 而且<b>不报错</b> —— 分数照算、Top-K 照出，只是排序全错。
/// </para>
/// </summary>
public static class SparseModelLoader
{
    public static async Task LoadIfNeededAsync(
        IServiceProvider services,
        string sparseModelPath,
        CancellationToken ct)
    {
        if (!File.Exists(sparseModelPath))
        {
            // dense / shuffle 不需要它；sparse / hybrid 会在编码时抛出带修复指引的异常
            return;
        }

        var loaded = await SparseModelStore.LoadAsync(sparseModelPath, ct);
        var encoder = services.GetRequiredService<Bm25SparseEncoder>();

        // 这个文件同时是索引清单：Week1 长在 InMemoryChunkIndex 上的同源校验，
        // 换到 Qdrant 后就没有载体了，由它接管。Week3 起多守一条规范化器一致。
        loaded.EnsureCompatibleWith(
            services.GetRequiredService<IEmbeddingService>(), encoder.NormalizerName);

        encoder.Load(loaded.Model);
    }
}
