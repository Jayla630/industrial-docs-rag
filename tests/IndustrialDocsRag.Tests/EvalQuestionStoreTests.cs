using IndustrialDocsRag.Eval;

namespace IndustrialDocsRag.Tests;

/// <summary>
/// 评测集文件是要人手编辑的（pooling 播种时往 anchors 里追加），
/// 所以读写必须是无损往返：写出去再读回来，内容一模一样。
/// </summary>
public class EvalQuestionStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"eval-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task 读写往返不丢字段()
    {
        var questions = new List<EvalQuestion>
        {
            new()
            {
                Qid = "q01",
                Stratum = Stratum.FaultCode,
                Query = "E201.2 的辅助码是多少",
                Anchors = [new Anchor(129, "0x22010201")],
            },
            new()
            {
                Qid = "q02",
                Stratum = Stratum.Prose,
                Query = "使用外接制动电阻要注意什么",
                Anchors = [new Anchor(58, "制动电阻"), new Anchor(59, "散热")],
            },
        };

        await EvalQuestionStore.SaveAsync(_path, questions);
        var loaded = await EvalQuestionStore.LoadAsync(_path);

        Assert.Equal(questions.Count, loaded.Count);

        foreach (var (expected, actual) in questions.Zip(loaded))
        {
            Assert.Equal(expected.Qid, actual.Qid);
            Assert.Equal(expected.Stratum, actual.Stratum);
            Assert.Equal(expected.Query, actual.Query);

            // Anchor 是全 primitive 成员的 positional record，结构相等成立；
            // 逐元素比而不是比整个 EvalQuestion，是因为后者的 Anchors 属性声明成
            // IReadOnlyList<Anchor>，record 自动生成的 Equals 对它走引用比较。
            Assert.Equal(expected.Anchors, actual.Anchors);
        }
    }

    /// <summary>
    /// 中文必须原样落盘。这个文件是人要读要改的，转义成 \uXXXX 就没法看了。
    /// </summary>
    [Fact]
    public async Task 中文不被转义()
    {
        await EvalQuestionStore.SaveAsync(_path, [new EvalQuestion
        {
            Qid = "q01",
            Stratum = Stratum.Prose,
            Query = "制动电阻",
            Anchors = [new Anchor(1, "电阻")],
        }]);

        Assert.Contains("制动电阻", await File.ReadAllTextAsync(_path));
    }

    /// <summary>
    /// qid 重复是人手编辑最容易犯的错，且后果是两道题的结果互相覆盖 —— 必须硬失败。
    /// </summary>
    [Fact]
    public async Task qid重复直接报错()
    {
        await File.WriteAllLinesAsync(_path,
        [
            """{"qid":"q01","stratum":"Prose","query":"a","anchors":[{"page":1,"needle":"x"}]}""",
            """{"qid":"q01","stratum":"Prose","query":"b","anchors":[{"page":2,"needle":"y"}]}""",
        ]);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => EvalQuestionStore.LoadAsync(_path));
        Assert.Contains("q01", ex.Message);
    }

    [Fact]
    public async Task 文件不存在时报错()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => EvalQuestionStore.LoadAsync(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.jsonl")));
    }

    [Fact]
    public async Task 空行被跳过()
    {
        await File.WriteAllLinesAsync(_path,
        [
            """{"qid":"q01","stratum":"Prose","query":"a","anchors":[{"page":1,"needle":"x"}]}""",
            "",
            "   ",
            """{"qid":"q02","stratum":"Prose","query":"b","anchors":[{"page":2,"needle":"y"}]}""",
        ]);

        Assert.Equal(2, (await EvalQuestionStore.LoadAsync(_path)).Count);
    }

    /// <summary>
    /// 报错信息里必须有<b>文件里的物理行号</b>。JsonException 自带的 LineNumber 是行内偏移，
    /// 会把人引到错误的位置——而评测集是手写的，语法错误是最常见的失败模式。
    /// </summary>
    [Fact]
    public async Task JSON语法错误报出文件行号()
    {
        await File.WriteAllLinesAsync(_path,
        [
            """{"qid":"q01","stratum":"Prose","query":"a","anchors":[{"page":1,"needle":"x"}]}""",
            """{"qid":"q02", 这里是坏的}""",
        ]);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => EvalQuestionStore.LoadAsync(_path));
        Assert.Contains("第 2 行", ex.Message);
    }
}
