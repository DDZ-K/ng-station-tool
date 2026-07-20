using NgStationTool.Services;

namespace NgStationTool;

/// <summary>无界面自检：图片复制 + 缓存闸门（不依赖真实键盘目标窗口）。</summary>
internal static class SelfTest
{
    public static int Run()
    {
        Console.WriteLine("NgStationTool self-test starting...");
        var root = Path.Combine(Path.GetTempPath(), "ng-station-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        var watch = Path.Combine(root, "watch");
        var output = Path.Combine(root, "out");
        var logs = Path.Combine(root, "logs");
        var archive = Path.Combine(root, "logs_done");
        Directory.CreateDirectory(watch);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(archive);

        var cfgPath = Path.Combine(root, "config.json");
        var cfg = new AppConfig
        {
            EnableImageCopy = true,
            EnableCloudRelease = true,
            WatchRoot = watch,
            OutputRoot = output,
            NgImageRoot = output,
            CloudLogRoot = logs,
            CloudLogArchiveRoot = archive,
            UseDateFolders = true,
            ReadyBudgetMs = 1000,
            FolderSettleMs = 400,
            BatchMaxWaitMs = 8000,
            SizeStableIntervalMs = 50,
            SizeStableChecks = 2,
            RetryDelayMs = 40,
            DebounceMs = 50,
            PendingTimeoutSec = 30,
            EnqueueFromImageCopyFolderName = true,
            EnqueueFromNgImageWatch = false, // 本测只测文件夹名入队，避免输出目录二次入队干扰
            LogReadyBudgetMs = 500,
            ResultLineNumber = 1,
            OkTokens = new List<string> { "OK" },
            NokTokens = new List<string> { "NOK" },
            OkKey = "NumPad9",
            NokKey = "NumPad7",
            AutoStartOnLaunch = false
        };
        cfg.Save(cfgPath);

        var log = new AppLogger(Path.Combine(root, "test_log.txt"), 200);
        var cache = new DmcPendingCache(log, cfg.PendingTimeoutSec);
        var kb = new KeyboardService(log);
        AppConfig live = cfg;
        var cloud = new CloudReleaseService(log, () => live, cache, kb);
        var img = new ImageCopyWatcher(log, () => live, (renamed, path, folder) =>
        {
            cloud.EnqueueDmc(renamed, "ImageCopyRenamed", path, folder);
        });

        var fail = 0;
        try
        {
            img.Start();
            cloud.Start();
            Thread.Sleep(800);

            // 1) 同夹写入 2 张图 → 静默后整夹一次拷贝，入 2 条 DMC
            var dmc = "DMCTEST001";
            var sub = Path.Combine(watch, dmc);
            Directory.CreateDirectory(sub);
            Thread.Sleep(200);
            // 最小 JPEG
            var payload = new byte[]
            {
                0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
                0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
            };
            var buf = new byte[256];
            Array.Copy(payload, buf, payload.Length);
            buf[^2] = 0xFF; buf[^1] = 0xD9;
            var jpg = Path.Combine(sub, "cam.jpg");
            var jpg2 = Path.Combine(sub, "cam2.jpg");
            File.WriteAllBytes(jpg, buf);
            Thread.Sleep(80);
            File.WriteAllBytes(jpg2, buf);

            var expected1 = dmc + "_cam";
            var expected2 = dmc + "_cam2";
            var deadline = DateTime.Now.AddSeconds(12);
            var copied = 0;
            while (DateTime.Now < deadline)
            {
                if (Directory.Exists(output))
                    copied = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                        .Count(f => Path.GetFileName(f).StartsWith(dmc + "_", StringComparison.OrdinalIgnoreCase));
                if (copied >= 2 && cache.Contains(expected1) && cache.Contains(expected2)) break;
                Thread.Sleep(100);
            }

            if (copied < 2)
            {
                Console.WriteLine("FAIL: batch copy expected 2 got " + copied);
                fail++;
            }
            else Console.WriteLine("PASS: batch copied 2 images");

            if (!cache.Contains(expected1) || !cache.Contains(expected2))
            {
                Console.WriteLine("FAIL: expected 2 DMCs, cache=" +
                    string.Join(",", cache.Snapshot().Select(x => x.Dmc)));
                fail++;
            }
            else Console.WriteLine("PASS: two renamed DMCs in cache");

            // 1b) 单张文件夹：只 1 张图也必须拷贝+入队（回归：单张不行）
            cache.ForceRemove(expected1, "selftest single prep");
            cache.ForceRemove(expected2, "selftest single prep");
            var dmcSolo = "DMCTEST_SOLO";
            var subSolo = Path.Combine(watch, dmcSolo);
            Directory.CreateDirectory(subSolo);
            Thread.Sleep(150);
            var soloJpg = Path.Combine(subSolo, "only.jpg");
            File.WriteAllBytes(soloJpg, buf);
            var expectedSolo = dmcSolo + "_only";
            deadline = DateTime.Now.AddSeconds(12);
            var soloOk = false;
            while (DateTime.Now < deadline)
            {
                if (cache.Contains(expectedSolo)
                    && Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                        .Any(f => Path.GetFileNameWithoutExtension(f)
                            .Equals(expectedSolo, StringComparison.OrdinalIgnoreCase)))
                {
                    soloOk = true;
                    break;
                }
                Thread.Sleep(100);
            }
            if (!soloOk)
            {
                Console.WriteLine("FAIL: single-image folder must copy+enqueue, cache=" +
                    string.Join(",", cache.Snapshot().Select(x => x.Dmc)));
                fail++;
            }
            else Console.WriteLine("PASS: single-image folder enqueued");

            // 1c) 同大小已存在时：重写源图时间戳后仍应再入队（不要求再拷一份）
            cache.ForceRemove(expectedSolo, "selftest same-size prep");
            Thread.Sleep(200);
            // 覆盖写入 → LastWriteTime 变新
            File.WriteAllBytes(soloJpg, buf);
            File.SetLastWriteTimeUtc(soloJpg, DateTime.UtcNow.AddSeconds(1));
            deadline = DateTime.Now.AddSeconds(12);
            var requeueOk = false;
            while (DateTime.Now < deadline)
            {
                if (cache.Contains(expectedSolo)) { requeueOk = true; break; }
                Thread.Sleep(100);
            }
            if (!requeueOk)
            {
                Console.WriteLine("FAIL: same-size re-write must re-enqueue single DMC");
                fail++;
            }
            else Console.WriteLine("PASS: same-size re-write re-enqueued");

            // 2) 无缓存时 log 应忽略
            img.Stop();
            cache.ForceRemove(expected1, "selftest gate");
            cache.ForceRemove(expected2, "selftest gate");
            cache.ForceRemove(expectedSolo, "selftest gate");
            Thread.Sleep(200);
            var orphanLog = Path.Combine(logs, expected1 + ".log");
            File.WriteAllText(orphanLog, "line1\nOK\n");
            Thread.Sleep(1500);
            if (cache.Contains(expected1))
            {
                Console.WriteLine("FAIL: orphan log must not create cache");
                fail++;
            }
            else Console.WriteLine("PASS: orphan log ignored (no cache)");

            // 3) 有缓存 + log（前缀+DMC）→ OK 键 + 单条组立刻回车
            var dmc2 = "DMCTEST002_cam";
            cache.TryEnqueue(dmc2, "selftest", jpg, folderKey: "solo");
            var logPath2 = Path.Combine(logs, "prefix_" + dmc2 + ".txt");
            File.WriteAllText(logPath2, "OK\n");

            deadline = DateTime.Now.AddSeconds(6);
            var left = true;
            while (DateTime.Now < deadline)
            {
                if (!cache.Contains(dmc2)) { left = false; break; }
                Thread.Sleep(100);
            }
            if (left)
            {
                Console.WriteLine("FAIL: DMC not removed after prefixed OK log");
                fail++;
            }
            else Console.WriteLine("PASS: DMC removed after prefixed OK log");

            // 4) 同文件夹两组：先判一条不应清完组；两条都判完才结束
            cache.ClearAll("selftest-before-folder");
            cache.TryEnqueue("FOLDER_IMG1", "selftest", null, folderKey: "FOLDER");
            cache.TryEnqueue("FOLDER_IMG2", "selftest", null, folderKey: "FOLDER");
            File.WriteAllText(Path.Combine(logs, "p_FOLDER_IMG1.txt"), "OK\n");
            deadline = DateTime.Now.AddSeconds(10);
            while (DateTime.Now < deadline && cache.Contains("FOLDER_IMG1")) Thread.Sleep(100);
            if (cache.Contains("FOLDER_IMG1") || !cache.Contains("FOLDER_IMG2") || cache.CountInFolder("FOLDER") != 1)
            {
                Console.WriteLine("FAIL: folder batch partial: " + string.Join(",", cache.Snapshot().Select(x => x.Dmc)));
                fail++;
            }
            else Console.WriteLine("PASS: first of folder judged, second still pending");

            File.WriteAllText(Path.Combine(logs, "p_FOLDER_IMG2.txt"), "OK\n");
            deadline = DateTime.Now.AddSeconds(10);
            while (DateTime.Now < deadline && cache.CountInFolder("FOLDER") > 0) Thread.Sleep(100);
            if (cache.CountInFolder("FOLDER") != 0)
            {
                Console.WriteLine("FAIL: folder not fully cleared remain=" + string.Join(",", cache.Snapshot().Select(x => x.Dmc)));
                try
                {
                    Console.WriteLine("--- logs dir ---");
                    foreach (var f in Directory.EnumerateFiles(logs)) Console.WriteLine(f + " | " + File.ReadAllText(f));
                    Console.WriteLine("--- test_log tail ---");
                    var tl = Path.Combine(root, "test_log.txt");
                    if (File.Exists(tl))
                    {
                        var lines = File.ReadAllLines(tl);
                        foreach (var line in lines.Skip(Math.Max(0, lines.Length - 40)))
                            Console.WriteLine(line);
                    }
                }
                catch (Exception ex) { Console.WriteLine(ex); }
                fail++;
            }
            else Console.WriteLine("PASS: folder all judged");

            var archived = Directory.Exists(archive) && Directory.EnumerateFiles(archive).Any(f =>
                Path.GetFileName(f).IndexOf(dmc2, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!archived && File.Exists(logPath2))
            {
                Console.WriteLine("FAIL: log not archived");
                fail++;
            }
            else Console.WriteLine("PASS: log archived or consumed");

            // 5) 超时清除
            cache.TryEnqueue("TIMEOUTX", "selftest", null, folderKey: "T");
            cache.SetTimeoutSec(1);
            Thread.Sleep(1500);
            cache.PurgeExpired();
            if (cache.Contains("TIMEOUTX"))
            {
                Console.WriteLine("FAIL: timeout purge");
                fail++;
            }
            else Console.WriteLine("PASS: timeout purge");
        }
        finally
        {
            try { img.Stop(); cloud.Stop(); } catch { /* ignore */ }
            try { img.Dispose(); cloud.Dispose(); } catch { /* ignore */ }
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }

        Console.WriteLine(fail == 0 ? "SELF_TEST: PASS" : $"SELF_TEST: FAIL count={fail}");
        return fail == 0 ? 0 : 1;
    }
}
