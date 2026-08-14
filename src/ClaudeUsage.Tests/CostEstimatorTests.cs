using ClaudeUsage.App.Services;

namespace ClaudeUsage.Tests;

public class CostEstimatorTests
{
    [Fact]
    public void RatesFor_未知モデルはSonnet相当()
    {
        var rates = CostEstimator.RatesFor("some-new-model");
        Assert.Equal(3.0, rates.Input);
        Assert.Equal(15.0, rates.Output);
    }

    [Fact]
    public void Estimate_トークン数に比例して増える()
    {
        var a = new ModelTotals { Input = 1_000_000, Output = 0 };
        var b = new ModelTotals { Input = 2_000_000, Output = 0 };
        Assert.True(CostEstimator.Estimate("sonnet", b) > CostEstimator.Estimate("sonnet", a));
    }

    [Fact]
    public void Estimate_キャッシュ読取は入力より安い()
    {
        var a = new ModelTotals { Input = 1_000_000, CacheRead = 0 };
        var b = new ModelTotals { Input = 0, CacheRead = 1_000_000 };
        Assert.True(CostEstimator.Estimate("opus", a) > CostEstimator.Estimate("opus", b));
    }

    [Fact]
    public void EstimateAll_合計を返す()
    {
        var week = new Dictionary<string, ModelTotals>
        {
            ["sonnet"] = new() { Input = 1_000_000 },
            ["opus"] = new() { Input = 1_000_000 },
        };
        var total = CostEstimator.EstimateAll(week);
        Assert.Equal(3.0 + 15.0, total, precision: 6);
    }
}
