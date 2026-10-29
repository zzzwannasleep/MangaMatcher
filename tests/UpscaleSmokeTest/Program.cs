using System.IO.Compression;
using System.Xml.Linq;
using KomgaScraper.Core;
using KomgaScraper.Core.Upscale;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace UpscaleSmokeTest;

/// <summary>
/// 图片超分自测（不联网、不真跑 GPU）：页序/解压/拼 CBZ/模型管理/显卡判定/命令行/任务流水线。
/// 用假引擎（FakeRunner）验证「顺序、WebP、ComicInfo、失败处理」，真引擎另做人工 E2E。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static string _root = "";

    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        _root = Path.Combine(Path.GetTempPath(), "KomgaUpscaleTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        try
        {
            TestOrdering();
            await TestArchiveExtractionAsync();
            await TestTarExtractionAsync();
            TestWebp();
            await TestCbzAndJobAsync();
            await TestJobFailureModesAsync();
            TestModelCatalog();
            await TestRuntimeAndModelsAsync();
            TestGpuDetection();
            TestVulkanParsing();
            TestArguments();
        }
        catch (Exception e)
        {
            Console.WriteLine($"UNCAUGHT: {e}");
            _failed++;
        }
        finally
        {
            try { Directory.Delete(_root, true); } catch { /* 忽略 */ }
        }

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "=== 全部通过 ===" : $"=== 失败 {_failed} 项 ===");
        return _failed == 0 ? 0 : 1;
    }

    // ================= 页序 =================

    private static void TestOrdering()
    {
        Section("自然排序");
        Check("2 < 10", ImageOrder.CompareNatural("2.jpg", "10.jpg") < 0);
        Check("1-2 < 1-10", ImageOrder.CompareNatural("1-2.jpg", "1-10.jpg") < 0);
        Check("第2话 < 第10话", ImageOrder.CompareNatural("第2话.png", "第10话.png") < 0);
        Check("大小写不敏感", ImageOrder.CompareNatural("a.jpg", "A.jpg") == 0);
        Check("前缀短的小", ImageOrder.CompareNatural("ch01", "ch01_2") < 0);

        var names = new[] { "10.jpg", "2.jpg", "1.jpg", "001.jpg", "cover.jpg" };
        var sorted = ImageOrder.Sort(names, s => s, ImageSortMode.NaturalName);
        Check("排序结果 1/001/2/10/cover", string.Join(",", sorted) == "1.jpg,001.jpg,2.jpg,10.jpg,cover.jpg");

        var archive = ImageOrder.Sort(names, s => s, ImageSortMode.ArchiveOrder);
        Check("包内顺序保持原样", archive.SequenceEqual(names));

        Check("图片扩展名", ImageOrder.IsImageFile("a.JPG") && ImageOrder.IsImageFile("a.webp") && !ImageOrder.IsImageFile("a.txt"));
        Check("macOS 垃圾", ImageOrder.IsJunk("__MACOSX/a.jpg") && ImageOrder.IsJunk("._001.jpg") && ImageOrder.IsJunk(".DS_Store"));

        Check("拒绝 ../ 路径", !ImageOrder.TryNormalizeRelativePath("../x.jpg", out _));
        Check("拒绝绝对路径", !ImageOrder.TryNormalizeRelativePath("/etc/x.jpg", out _));
        Check("拒绝盘符", !ImageOrder.TryNormalizeRelativePath("C:/x.jpg", out _));
        Check("接受正常路径", ImageOrder.TryNormalizeRelativePath("./vol1\\001.jpg", out var norm) && norm == "vol1/001.jpg");

        Check("文件名清洗", ImageOrder.SanitizeFileName("a/b:c*?") == "a_b_c__");
        Check("空标题回退", ImageOrder.SanitizeFileName("   ") == "未命名");
        Check("设备名避开", ImageOrder.SanitizeFileName("CON") == "CON_");
    }

    // ================= 解压 =================

    private static async Task TestArchiveExtractionAsync()
    {
        Section("压缩包解压");
        var zipPath = Path.Combine(_root, "in.zip");
        var dest = Path.Combine(_root, "extract");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddEntry(zip, "vol1/10.png", Png(30, 40, 10, 20, 30));
            AddEntry(zip, "vol1/2.png", Png(30, 40, 10, 20, 30));
            AddEntry(zip, "vol1/1.png", Png(30, 40, 10, 20, 30));
            AddEntry(zip, "vol1/1.png", Png(30, 40, 99, 20, 30));   // 重名 → 自动加 (2)
            AddEntry(zip, "__MACOSX/._1.png", Png(4, 4, 0, 0, 0));
            AddEntry(zip, "readme.txt", "hello"u8.ToArray());
            AddEntry(zip, "../evil.png", Png(4, 4, 0, 0, 0));
        }

        var pages = await ComicArchiveReader.ExtractAsync(zipPath, dest, ImageSortMode.NaturalName,
            NullLogger.Instance, CancellationToken.None);

        Check("页数 = 4（重名保留 + 垃圾/越界被过滤）", pages.Count == 4);
        Check("自然排序：1 / 1 (2) / 2 / 10",
            string.Join(",", pages.Select(p => p.DisplayName)) == "1.png,1_2.png,2.png,10.png");
        Check("尺寸识别", pages.All(p => p.Width == 30 && p.Height == 40));
        Check("没有写出目标目录", !File.Exists(Path.Combine(_root, "evil.png")));

        var archiveOrder = await ComicArchiveReader.ExtractAsync(zipPath, Path.Combine(_root, "extract2"),
            ImageSortMode.ArchiveOrder, NullLogger.Instance, CancellationToken.None);
        Check("包内顺序 = 写入顺序", archiveOrder.Select(p => p.DisplayName)
            .SequenceEqual(["10.png", "2.png", "1.png", "1_2.png"]));

        // 不支持的后缀
        var bad = Path.Combine(_root, "in.gz");
        await File.WriteAllTextAsync(bad, "x");
        await ExpectThrowsAsync<NotSupportedException>(() =>
            ComicArchiveReader.ExtractAsync(bad, Path.Combine(_root, "e3"), ImageSortMode.NaturalName,
                NullLogger.Instance, CancellationToken.None), "不支持的格式给出明确异常");
    }

    // ================= WebP =================

    /// <summary>tar 走 SharpCompress 分支（rar/7z 同一条代码路径）。</summary>
    private static async Task TestTarExtractionAsync()
    {
        Section("tar 解压（SharpCompress）");
        var p1 = Path.Combine(_root, "tar1.png");
        var p2 = Path.Combine(_root, "tar2.png");
        File.WriteAllBytes(p1, Png(20, 30, 1, 2, 3));
        File.WriteAllBytes(p2, Png(20, 30, 4, 5, 6));
        var tarPath = Path.Combine(_root, "in.tar");
        await using (var fs = File.Create(tarPath))
        await using (var writer = new System.Formats.Tar.TarWriter(fs))
        {
            writer.WriteEntry(p1, "pages/2.png");
            writer.WriteEntry(p2, "pages/1.png");
        }

        var pages = await ComicArchiveReader.ExtractAsync(tarPath, Path.Combine(_root, "tar-out"),
            ImageSortMode.NaturalName, NullLogger.Instance, CancellationToken.None);
        Check("tar 也能解压并按名排序", pages.Count == 2 && pages[0].DisplayName == "1.png");
    }

    private static void TestWebp()
    {
        Section("WebP q95");
        var src = Path.Combine(_root, "src.png");
        File.WriteAllBytes(src, Png(400, 600, 120, 130, 140));
        var dst = Path.Combine(_root, "out.webp");
        var (w, h) = WebpTranscoder.Encode(src, dst, 95, 0);
        var bytes = File.ReadAllBytes(dst);
        Check("尺寸不变", w == 400 && h == 600);
        Check("RIFF/WEBP 头", bytes.Length > 12 &&
                             System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" &&
                             System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP");

        var capped = Path.Combine(_root, "capped.webp");
        var (cw, ch) = WebpTranscoder.Encode(src, capped, 95, 200);
        Check("长边上限生效", Math.Max(cw, ch) == 200 && cw < 400 && ch < 600);
    }

    // ================= CBZ / Job =================

    private static async Task TestCbzAndJobAsync()
    {
        Section("超分任务 + CBZ + ComicInfo");
        var (runtime, model) = CreateFakeRuntime("job1");
        var outDir = Path.Combine(_root, "out1");
        var pages = new List<UpscalePageInput>();
        for (var i = 0; i < 3; i++)
        {
            var p = Path.Combine(_root, $"page{i}.png");
            File.WriteAllBytes(p, Png(60, 90, 40 + i * 60, 20, 200 - i * 50));
            pages.Add(new UpscalePageInput(p, $"{i + 1:000}.png", new FileInfo(p).Length, 60, 90));
        }

        var job = new UpscaleJob(runtime, new FakeRunner(), NullLogger.Instance);
        var result = await job.RunAsync(pages, model, new UpscaleOptions { Scale = 4, WebpQuality = 95 },
            new UpscaleMetadata("测试标题", "测试系列", "测试作者", "简介", "zh", "1"),
            outDir, null, CancellationToken.None);

        Check("返回页数", result.Pages == 3 && result.Skipped == 0);
        Check("CBZ 存在", File.Exists(result.OutputPath) && result.OutputPath.EndsWith(".cbz"));
        Check("输出体积 > 0", result.OutputBytes > 0 && result.InputBytes > 0);

        using var zip = ZipFile.OpenRead(result.OutputPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Check("条目顺序 ComicInfo + 0001..0003", names.SequenceEqual(
            ["ComicInfo.xml", "0001.webp", "0002.webp", "0003.webp"]));

        var infoEntry = zip.GetEntry("ComicInfo.xml")!;
        using var reader = new StreamReader(infoEntry.Open());
        var xml = await reader.ReadToEndAsync();
        var doc = XDocument.Parse(xml);
        Check("ComicInfo Title", doc.Root?.Element("Title")?.Value == "测试标题");
        Check("ComicInfo Series", doc.Root?.Element("Series")?.Value == "测试系列");
        Check("ComicInfo PageCount", doc.Root?.Element("PageCount")?.Value == "3");
        Check("ComicInfo Manga", doc.Root?.Element("Manga")?.Value == "YesAndRightToLeft");
        Check("ComicInfo Notes 记录参数", (doc.Root?.Element("Notes")?.Value ?? "").Contains("WebP q95"));
        Check("ComicInfo Pages 3 条", doc.Root?.Element("Pages")?.Elements("Page").Count() == 3);

        // 逐页核对颜色，确认页序没有错位（源图 i=0 红 40、i=1 红 100、i=2 红 160）
        var expectedReds = new[] { 40, 100, 160 };
        for (var i = 0; i < 3; i++)
        {
            using var es = zip.GetEntry($"{i + 1:0000}.webp")!.Open();
            using var img = Image.Load<Rgba32>(es);
            var px = img[img.Width / 2, img.Height / 2];
            Check($"第 {i + 1} 页颜色对应源图 {i + 1}", Math.Abs(px.R - expectedReds[i]) <= 8 &&
                                                       Math.Abs(px.B - (200 - i * 50)) <= 8);
        }
    }

    private static async Task TestJobFailureModesAsync()
    {
        Section("失败处理");
        var (runtime, model) = CreateFakeRuntime("job2");
        var pages = new List<UpscalePageInput>();
        for (var i = 0; i < 3; i++)
        {
            var p = Path.Combine(_root, $"fail{i}.png");
            File.WriteAllBytes(p, Png(40, 60, 10, 10, 10));
            pages.Add(new UpscalePageInput(p, $"{i + 1:000}.png", new FileInfo(p).Length, 40, 60));
        }

        // 第 2 页失败 + 跳过 → 仍出 2 页 CBZ
        var skipDir = Path.Combine(_root, "out-skip");
        var skipJob = new UpscaleJob(runtime, new FakeRunner { FailOn = "fail1" }, NullLogger.Instance);
        var skipResult = await skipJob.RunAsync(pages, model,
            new UpscaleOptions { Scale = 4, SkipFailedPages = true },
            new UpscaleMetadata("跳过测试"), skipDir, null, CancellationToken.None);
        Check("跳过失败页后仍导出", skipResult.Pages == 2 && skipResult.Skipped == 1 &&
                                    skipResult.Warnings.Count == 1 && File.Exists(skipResult.OutputPath));

        // 第 2 页失败 + 不跳过 → 抛异常且不产出文件
        var abortDir = Path.Combine(_root, "out-abort");
        var abortJob = new UpscaleJob(runtime, new FakeRunner { FailOn = "fail1" }, NullLogger.Instance);
        await ExpectThrowsAsync<UpscaleJobException>(() => abortJob.RunAsync(pages, model,
            new UpscaleOptions { Scale = 4 },
            new UpscaleMetadata("中止测试"), abortDir, null, CancellationToken.None),
            "单页失败默认中止整次任务");
        Check("中止后没有半成品 CBZ", !File.Exists(Path.Combine(abortDir, "中止测试.cbz")));

        // 取消
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelJob = new UpscaleJob(runtime, new FakeRunner(), NullLogger.Instance);
        await ExpectThrowsAsync<OperationCanceledException>(() => cancelJob.RunAsync(pages, model,
            new UpscaleOptions { Scale = 4 }, new UpscaleMetadata("取消测试"),
            Path.Combine(_root, "out-cancel"), null, cts.Token), "取消能中断任务");

        // 同名自动改名
        var dupDir = Path.Combine(_root, "out-dup");
        var dupJob = new UpscaleJob(runtime, new FakeRunner(), NullLogger.Instance);
        var first = await dupJob.RunAsync(pages, model, new UpscaleOptions { Scale = 4 },
            new UpscaleMetadata("同名测试"), dupDir, null, CancellationToken.None);
        var second = await dupJob.RunAsync(pages, model, new UpscaleOptions { Scale = 4 },
            new UpscaleMetadata("同名测试"), dupDir, null, CancellationToken.None);
        Check("同名输出自动加 (2)", first.OutputPath != second.OutputPath && second.OutputPath.Contains("(2)"));

        // 覆盖
        var overwrite = await dupJob.RunAsync(pages, model,
            new UpscaleOptions { Scale = 4, Overwrite = true },
            new UpscaleMetadata("同名测试"), dupDir, null, CancellationToken.None);
        Check("Overwrite 直接覆盖", overwrite.OutputPath == first.OutputPath);

        // 显式输出路径（界面会用它避开与源压缩包同名）
        var explicitPath = Path.Combine(_root, "explicit", "自定义名.cbz");
        var explicitResult = await dupJob.RunAsync(pages, model,
            new UpscaleOptions { Scale = 4, OutputPath = explicitPath },
            new UpscaleMetadata("忽略标题"), Path.Combine(_root, "whatever"), null, CancellationToken.None);
        Check("显式 OutputPath 生效", explicitResult.OutputPath == explicitPath && File.Exists(explicitPath));

        // 4x 后像素过大：直接拦下并提示设置长边上限，别等 ImageSharp 吃光内存
        var huge = new List<UpscalePageInput>
        {
            new(pages[0].FilePath, "huge.png", 100, 20000, 20000),
        };
        await ExpectThrowsAsync<UpscaleSetupException>(() => dupJob.RunAsync(huge, model,
                new UpscaleOptions { Scale = 4 }, new UpscaleMetadata("过大"), Path.Combine(_root, "out-huge"),
                null, CancellationToken.None),
            "超大输出被提前拦截");
    }

    // ================= 模型目录 / 运行时 =================

    private static void TestModelCatalog()
    {
        Section("模型清单");
        Check("官方三个模型", UpscaleModelCatalog.Official.Count == 3);
        Check("轻量 2/3/4", UpscaleModelCatalog.Find(UpscaleModelCatalog.LightweightId)!.Scales.SequenceEqual([2, 3, 4]));
        Check("普适只支持 4x", UpscaleModelCatalog.Find(UpscaleModelCatalog.GeneralId)!.Scales.SequenceEqual([4]));
        Check("特化只支持 4x", UpscaleModelCatalog.Find(UpscaleModelCatalog.SpecializedId)!.Scales.SequenceEqual([4]));

        var light = UpscaleModelCatalog.Find(UpscaleModelCatalog.LightweightId)!;
        Check("轻量模型文件名带 -x2", UpscaleRuntime.ModelFileNames(light, 2)
            .SequenceEqual(["realesr-animevideov3-x2.param", "realesr-animevideov3-x2.bin"]));
        var anime = UpscaleModelCatalog.Find(UpscaleModelCatalog.SpecializedId)!;
        Check("特化模型文件名不带后缀", UpscaleRuntime.ModelFileNames(anime, 4)
            .SequenceEqual(["realesrgan-x4plus-anime.param", "realesrgan-x4plus-anime.bin"]));
    }

    private static async Task TestRuntimeAndModelsAsync()
    {
        Section("引擎安装 / 自定义模型");
        var packZip = Path.Combine(_root, "pack.zip");
        using (var zip = ZipFile.Open(packZip, ZipArchiveMode.Create))
        {
            const string dir = "realesrgan-ncnn-vulkan-20220424-windows/";
            AddEntry(zip, dir + "realesrgan-ncnn-vulkan.exe", "MZfake"u8.ToArray());
            AddEntry(zip, dir + "vcomp140.dll", "dll"u8.ToArray());
            AddEntry(zip, dir + "models/realesr-animevideov3-x2.param", "7767517"u8.ToArray());
            AddEntry(zip, dir + "models/realesr-animevideov3-x2.bin", new byte[2048]);
            AddEntry(zip, dir + "models/realesrgan-x4plus.param", "7767517"u8.ToArray());
            AddEntry(zip, dir + "models/realesrgan-x4plus.bin", new byte[2048]);
            AddEntry(zip, dir + "models/realesrgan-x4plus-anime.param", "7767517"u8.ToArray());
            AddEntry(zip, dir + "models/realesrgan-x4plus-anime.bin", new byte[2048]);
        }

        var root = Path.Combine(_root, "runtime");
        var runtime = new UpscaleRuntime(root);
        Check("安装前未就绪", !runtime.EngineReady && runtime.InstalledVersion == "未安装");

        await runtime.InstallPackFromZipAsync(packZip, NullLogger.Instance);
        Check("安装后引擎就绪", runtime.EngineReady && runtime.InstalledVersion == UpscaleRuntime.PackVersion);
        Check("轻量 2x 就绪", runtime.IsModelReady(UpscaleModelCatalog.Find(UpscaleModelCatalog.LightweightId)!, 2));
        Check("特化 4x 就绪", runtime.IsModelReady(UpscaleModelCatalog.Find(UpscaleModelCatalog.SpecializedId)!, 4));
        Check("轻量 3x 缺文件（zip 里没放）",
            runtime.MissingModelFiles(UpscaleModelCatalog.Find(UpscaleModelCatalog.LightweightId)!, 3)
                .Contains("realesr-animevideov3-x3.param"));

        // 自定义模型
        var param = Path.Combine(_root, "MyModel.param");
        var bin = Path.Combine(_root, "MyModel.bin");
        await File.WriteAllTextAsync(param, "7767517\n1 1\n");
        await File.WriteAllBytesAsync(bin, new byte[4096]);
        var custom = await runtime.ImportCustomModelAsync(param, bin, "My Model", 4);
        Check("自定义模型 id 合法", custom.Id == "my_model");
        Check("自定义模型可枚举", runtime.CustomModels().Any(m => m.Id == custom.Id && m.Scales.SequenceEqual([4])));
        Check("自定义模型就绪", runtime.IsModelReady(custom, 4));
        Check("自定义模型倍率不符则不就绪", !runtime.IsModelReady(custom, 2));

        // 坏 param 拒绝
        await File.WriteAllTextAsync(param, "not-a-model");
        await ExpectThrowsAsync<InvalidDataException>(
            () => runtime.ImportCustomModelAsync(param, bin, "Bad", 4), "拒绝非 ncnn 的 .param");

        Check("删除自定义模型", runtime.RemoveCustomModel(custom.Id) && !runtime.CustomModels().Any(m => m.Id == custom.Id));
        Check("不能删除官方模型", !runtime.RemoveCustomModel(UpscaleModelCatalog.GeneralId));

        // 同名导入自动加序号
        await File.WriteAllTextAsync(param, "7767517\n1 1\n");
        var a = await runtime.ImportCustomModelAsync(param, bin, "dup", 4);
        var b = await runtime.ImportCustomModelAsync(param, bin, "dup", 4);
        Check("同名自定义模型自动编号", a.Id == "dup" && b.Id == "dup-2");

        // 不允许占用官方模型 id，避免覆盖官方权重
        var collide = await runtime.ImportCustomModelAsync(param, bin, "realesrgan-x4plus", 4);
        Check("自定义模型避开官方 id",
            collide.Id != UpscaleModelCatalog.GeneralId &&
            runtime.IsModelReady(UpscaleModelCatalog.Find(UpscaleModelCatalog.GeneralId)!, 4));
    }

    // ================= 显卡 =================

    private static void TestGpuDetection()
    {
        Section("显卡判定");
        Check("NVIDIA → 独显", GpuDetector.Classify("NVIDIA GeForce RTX 5060 Laptop GPU", "PCI\\VEN_10DE&DEV_2D19") == GpuClass.Discrete);
        Check("Intel UHD → 核显", GpuDetector.Classify("Intel(R) UHD Graphics 770", "PCI\\VEN_8086&DEV_468B") == GpuClass.Integrated);
        Check("Intel Arc A770 → 独显", GpuDetector.Classify("Intel(R) Arc(TM) A770 Graphics", "PCI\\VEN_8086&DEV_56A0") == GpuClass.Discrete);
        Check("Intel Arc 核显 → 核显", GpuDetector.Classify("Intel(R) Arc(TM) Graphics", "PCI\\VEN_8086&DEV_7D55") == GpuClass.Integrated);
        Check("AMD 核显", GpuDetector.Classify("AMD Radeon(TM) Graphics", "PCI\\VEN_1002&DEV_15BF") == GpuClass.Integrated);
        Check("AMD 独显", GpuDetector.Classify("AMD Radeon RX 7900 XTX", "PCI\\VEN_1002&DEV_744C") == GpuClass.Discrete);
        Check("虚拟显示器识别", GpuDetector.LooksVirtual("GameViewer Virtual Display Adapter") &&
                             GpuDetector.LooksVirtual("Microsoft Basic Display Adapter"));
        Check("厂商解析", GpuDetector.VendorOf("PCI\\VEN_10DE&DEV_1") == "nvidia" &&
                        GpuDetector.VendorOf("PCI\\VEN_1002&DEV_1") == "amd" &&
                        GpuDetector.VendorOf("PCI\\VEN_8086&DEV_1") == "intel");

        var report = GpuDetector.Detect();   // 真机调用不应抛异常
        Check("Detect 返回结果", report.Adapters.Count >= 0);
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine($"    本机：{report.Summary}");
            if (report.NeedsWarning) Console.WriteLine($"    提醒：{report.Advice.Replace("\n", " ")}");
        }
    }

    private static void TestVulkanParsing()
    {
        Section("Vulkan 设备探测");
        const string stderr = """
                              [0 Intel(R) UHD Graphics 770]  queueC=0[1]  queueG=0[1]  queueT=0[1]
                              [1 NVIDIA GeForce RTX 5060 Laptop GPU]  queueC=2[8]  queueG=0[16]  queueT=1[2]
                              [2 Intel(R) UHD Graphics 770]  queueC=0[1]  queueG=0[1]  queueT=0[1]
                              invalid gpu device
                              """;
        var devices = VulkanProbe.Parse(stderr);
        Check("解析出 3 个设备", devices.Count == 3);
        Check("设备名正确", devices[1].Name == "NVIDIA GeForce RTX 5060 Laptop GPU");
        Check("推荐独显编号", VulkanProbe.FindBestDevice(devices, "NVIDIA GeForce RTX 5060 Laptop GPU") == 1);
        Check("找不到返回 null", VulkanProbe.FindBestDevice(devices, "No Such GPU") is null);
        Check("空输入返回空", VulkanProbe.Parse("").Count == 0);
    }

    private static void TestArguments()
    {
        Section("引擎命令行");
        var inv = new UpscaleInvocation(@"C:\e\r.exe", @"C:\e\models", "realesrgan-x4plus-anime",
            4, 128, true, 1, @"C:\in\1.png", @"C:\out\1.png");
        var args = ProcessUpscalerRunner.BuildArguments(inv);
        Check("包含 -i/-o", args.Contains(@"C:\in\1.png") && args.Contains(@"C:\out\1.png"));
        Check("包含 -s 4", string.Join(" ", args).Contains("-s 4"));
        Check("包含 -t 128", string.Join(" ", args).Contains("-t 128"));
        Check("包含 -x", args.Contains("-x"));
        Check("包含 -g 1", string.Join(" ", args).Contains("-g 1"));

        var noGpu = new UpscaleInvocation("e", "m", "m", 2, 0, false, null, "i", "o");
        Check("自动显卡不传 -g", !ProcessUpscalerRunner.BuildArguments(noGpu).Contains("-g"));
    }

    // ================= 工具 =================

    private static (UpscaleRuntime Runtime, UpscaleModelInfo Model) CreateFakeRuntime(string name)
    {
        var root = Path.Combine(_root, "rt-" + name);
        var runtime = new UpscaleRuntime(root);
        Directory.CreateDirectory(runtime.EngineDir);
        Directory.CreateDirectory(runtime.ModelsDir);
        File.WriteAllText(runtime.ExePath, "fake");
        var model = UpscaleModelCatalog.Find(UpscaleModelCatalog.SpecializedId)!;
        var files = UpscaleRuntime.ModelFileNames(model, 4);
        File.WriteAllText(Path.Combine(runtime.ModelsDir, files[0]), "7767517");
        File.WriteAllBytes(Path.Combine(runtime.ModelsDir, files[1]), new byte[2048]);
        return (runtime, model);
    }

    private static byte[] Png(int w, int h, int r, int g, int b)
    {
        using var img = new Image<Rgba32>(w, h, new Rgba32((byte)r, (byte)g, (byte)b));
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] data)
    {
        var e = zip.CreateEntry(name);
        using var s = e.Open();
        s.Write(data);
    }

    private static async Task ExpectThrowsAsync<T>(Func<Task> action, string what) where T : Exception
    {
        try
        {
            await action();
            Fail($"{what}（没有抛异常）");
        }
        catch (T)
        {
            Pass(what);
        }
        catch (Exception e)
        {
            Fail($"{what}（抛了 {e.GetType().Name}：{e.Message}）");
        }
    }

    private static void Section(string name) => Console.WriteLine($"\n--- {name} ---");

    private static void Check(string name, bool ok)
    {
        if (ok) Pass(name);
        else Fail(name);
    }

    private static void Pass(string name)
    {
        _passed++;
        Console.WriteLine($"  ok  {name}");
    }

    private static void Fail(string name)
    {
        _failed++;
        Console.WriteLine($"  FAIL {name}");
    }

    /// <summary>模拟引擎：把输入按 2 倍放大成 PNG，可指定某个文件失败。</summary>
    private sealed class FakeRunner : IUpscalerRunner
    {
        public string? FailOn { get; init; }

        public Task RunAsync(UpscaleInvocation invocation, Action<string>? log, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailOn is not null && Path.GetFileName(invocation.InputPath).Contains(FailOn))
                throw new InvalidOperationException("模拟超分失败");

            using var img = Image.Load(invocation.InputPath);
            img.Mutate(x => x.Resize(img.Width * 2, img.Height * 2));
            img.SaveAsPng(invocation.OutputPath);
            return Task.CompletedTask;
        }
    }
}
