using System;
using System.Threading.Tasks;

[TestFixture]
public class MathToolsTests
{
    [OneTimeSetUp]
    public void OneTime() => Console.WriteLine("MathToolsTests OneTimeSetUp");

    [SetUp]
    public void SetUp() => Console.WriteLine("MathToolsTests SetUp");

    [TearDown]
    public void TearDown() => Console.WriteLine("MathToolsTests TearDown");

    [OneTimeTearDown]
    public void OneTimeTearDown() => Console.WriteLine("MathToolsTests OneTimeTearDown");

    [Test]
    public void FastExp_BasicValues_ReturnsCorrect()
    {
        var r = ElGamalEncryption.MathTools.FastExp(2, 10, 1000);
        Assert.AreEqual(1024 % 1000, r);
    }

    [TestCase(3, 3, 7, 27 % 7)]
    [TestCase(2, 5, 13, 32 % 13)]
    public void FastExp_TestCases(long a, long b, long n, long expected)
    {
        var r = ElGamalEncryption.MathTools.FastExp(a, b, n);
        Assert.AreEqual(expected, r);
    }

    [Test]
    public void ModInverse_NonCoprime_Throws()
    {
        Assert.Throws<ArgumentException>(() => ElGamalEncryption.MathTools.ModInverse(6, 9));
    }

    [Test]
    public async Task FastPowMulAsync_Wrap()
    {
        var res = await Task.Run(() => ElGamalEncryption.MathTools.FastPowMul(2, 3, 4, 13));
        Assert.AreEqual((4 * ElGamalEncryption.MathTools.FastExp(2, 3, 13)) % 13, res);
    }

    [Test]
    public void IsPrime_Checks()
    {
        Assert.IsTrue(ElGamalEncryption.MathTools.IsPrime(13));
        Assert.IsFalse(ElGamalEncryption.MathTools.IsPrime(15));
    }
}

[TestFixture]
public class ElGamalTests
{
    private ElGamalEncryption.ElGamal eg;

    [SetUp]
    public void SetUp() { eg = new ElGamalEncryption.ElGamal(); }

    [Test]
    public void InitializeElGamal_ComputesYandA()
    {
        eg.InitializeElGamal(23, 5, 6, 7); 
        var expectedY = (int)ElGamalEncryption.MathTools.FastExp(5, 6, 23);
        Assert.AreEqual(expectedY, eg.Y);
    }

    [Test]
    public void StartEncryption_ValueTooLarge_ReturnsNull()
    {
        eg.InitializeElGamal(13, 2, 3, 5);
        var data = new byte[] { 15 }; 
        var res = eg.StartEncryption(data);
        Assert.IsNull(res);
    }

    [Test]
    public void StartDecryption_InvalidByteArray_ReturnsNull()
    {
        var bad = new byte[] { 1, 2, 3 }; 
        var res = eg.StartDecryption(bad);
        Assert.IsNull(res);
    }

    [Test]
    public void EncryptThenDecrypt_RoundtripEqualsOriginal()
    {
        eg.InitializeElGamal(23, 5, 6, 7);
        var payload = new byte[] { 2, 3, 4 };
        var cipher = eg.StartEncryption(payload);
        Assert.IsNotNull(cipher);
        var plain = eg.StartDecryption(cipher);
        Assert.IsNotNull(plain);
        Assert.SequenceEqual(payload, plain);
    }
}

[TestFixture]
public class SharedContextTests
{
    [OneTimeSetUp]
    public void OneTime()
    {
        SharedContextStore.Register("config", new Dictionary<string, string> { ["mode"] = "test" });
    }

    [Test]
    public void DirectAccessSharedContext()
    {
        Assert.IsTrue(SharedContextStore.TryGet<Dictionary<string, string>>("config", out var cfg));
        Assert.IsNotNull(cfg);
        Assert.IsTrue(cfg["mode"] == "test");
    }

    [Test(Priority = -1)]
    public void InjectionByParameter([SharedContext("config")] Dictionary<string, string> cfg)
    {
        Assert.IsNotNull(cfg);
        Assert.IsTrue(cfg.ContainsKey("mode"));
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        SharedContextStore.Clear();

        if (SharedContextStore.TryGet<Dictionary<string, string>>("config", out var cfg))
        {
            SharedContextStore.Unregister("config");
        }
        
    }
}

[TestFixture]
public class ExtraAssertsAndAsyncTests
{
    [Test]
    public void AreNotEqual_Contains_DoesNotContain_GreaterLess()
    {
        Assert.AreEqual(5, 2 + 2);

        var list = new List<int> { 1, 2, 3, 5 };
        Assert.Contains(list, 3);
        Assert.DoesNotContain(list, 4);

        Assert.GreaterThan(10, 5);
        Assert.LessThan(3, 7);
    }

    [TestTimeout(500)]
    [Test]
    public async Task FastExpAsync_TaskWorks()
    {
        var t = ElGamalEncryption.MathTools.FastExpAsync(2, 10, 1000);
        var res = await t;
        Assert.AreEqual(1024 % 1000, res);
    }

    [TestTimeout(1000)]
    [Test(Skip = false, Reason = "Временно нестабилен")]
    public async Task FastExpValueTask_Works()
    {
        var vt = ElGamalEncryption.MathTools.FastExpValueTask(2, 10, 1000);
        var res = await vt;
        Assert.AreEqual(1024 % 1000, res);
    }

    [Test]
    public async Task ModInverseAsync_ThrowsAsync()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await ElGamalEncryption.MathTools.ModInverseAsync(6, 9));
    }

    [Test]
    public async Task ModInverseValueTask_ThrowsAsync()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await ElGamalEncryption.MathTools.ModInverseValueTask(6, 9));
    }

    [Test(Skip = false, Reason = "Временно нестабилен")]
    public void SequenceAndNullAndNotNullExample()
    {
        byte[] arr = null;
        Assert.IsNull(arr);

        byte[] arr2 = new byte[] { 1, 2 };
        Assert.IsNotNull(arr2);
        Assert.SequenceEqual(new byte[] { 1, 2 }, arr2);
    }
}
