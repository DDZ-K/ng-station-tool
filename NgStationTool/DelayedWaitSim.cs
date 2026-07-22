using NgStationTool.Services;

namespace NgStationTool;

/// <summary>
/// 仿真：图片先改名进暂存，Waiting 长时间匹配不上，约 1 分钟后再匹配成功 → 验证 DMC 仍能入队。
/// 运行：NgStationTool.exe --sim-delayed-wait
/// 可选：--sim-delayed-wait 15  （秒，默认 60）
/// </summary>
internal static class DelayedWaitSim
{
    public static int Run(string[] args)
    {
        var delaySec = 60;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--sim-delayed-wait", StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length
                && int.TryParse(args[i + 1], out var s)
                && s >= 0)
            {
                delaySec = s;
            }
        }

        Console.WriteLine("=== Delayed Waiting match simulation ===");
        Console.WriteLine($"Scenario: stage renamed images, NO wait match for {delaySec}s, then match → DMC enqueue?");
        Console.WriteLine();

        var root = Path.Combine(Path.GetTempPath(), "ng-delayed-wait-" + Guid.NewGuid().ToString("N")[..8]);
        var watch = Path.Combine(root, "watch");
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(watch);
        Directory.CreateDirectory(output);

        var cfg = new AppConfig
        {
            EnableImageCopy = true,
            EnableCloudRelease = true,
            EnableHaranUiGate = true,
            HaranSerialSessions = true,
            HaranNextSessionDelayMs = 500,
            WatchRoot = watch,
            OutputRoot = output,
            UseDateFolders = false,
            EnqueueFromImageCopyFolderName = true,
            SkipIfSameSizeExists = false,
            PendingTimeoutSec = 180
        };
        AppConfig live = cfg;

        var log = new AppLogger(Path.Combine(root, "sim_log.txt"), 500);
        var cache = new DmcPendingCache(log, cfg.PendingTimeoutSec);
        var session = new JudgmentSessionCoordinator(log, () => live);
        var enqueued = new List<string>();

        var img = new ImageCopyWatcher(log, () => live, (stem, path, folder) =>
        {
            enqueued.Add(stem);
            cache.TryEnqueue(stem, "sim-delayed", path, folder);
            Console.WriteLine($"  [DMC enqueue] {stem} folder={folder}");
        });

        var jpeg = new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
            0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
        };
        var pad = new byte[64];
        Array.Copy(jpeg, pad, jpeg.Length);

        const string folder = "LotDelayed_NG";
        var fail = 0;
        try
        {
            // 1) 三张图改名进暂存（模拟整夹就绪但界面还不是 Waiting）
            for (var i = 1; i <= 3; i++)
            {
                var dir = Path.Combine(watch, folder);
                Directory.CreateDirectory(dir);
                var src = Path.Combine(dir, $"cam{i}.jpg");
                File.WriteAllBytes(src, pad);
                img.EnqueueStagedForTest(folder, src, $"{folder}_cam{i}.jpg");
            }
            Console.WriteLine($"[1] Staged 3 renamed images, StagedCount={img.StagedCount}, DMC cache={cache.Count}");
            if (cache.Count != 0)
            {
                Console.WriteLine("FAIL: DMC must NOT enqueue before Waiting match");
                fail++;
            }
            else Console.WriteLine("PASS: no DMC before Waiting");

            // 2) 长时间匹配不上：不调用 Flush / 不开会话 Flush
            Console.WriteLine($"[2] Waiting NOT matched — hold staged for {delaySec}s (no Flush)...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var nextPrint = 10;
            while (sw.Elapsed.TotalSeconds < delaySec)
            {
                Thread.Sleep(500);
                // 模拟轮询但一直 Unknown / 非 Waiting：不 Flush
                if (sw.Elapsed.TotalSeconds >= nextPrint)
                {
                    Console.WriteLine(
                        $"  ... t={sw.Elapsed.TotalSeconds:F0}s staged={img.StagedCount} dmc={cache.Count}");
                    nextPrint += 10;
                }
            }
            sw.Stop();
            Console.WriteLine($"[3] After {sw.Elapsed.TotalSeconds:F1}s still staged={img.StagedCount} dmc={cache.Count}");
            if (img.StagedCount != 3)
            {
                Console.WriteLine("FAIL: staged should still be 3 after delay");
                fail++;
            }
            else Console.WriteLine("PASS: staged kept for full delay");
            if (cache.Count != 0)
            {
                Console.WriteLine("FAIL: DMC leaked during unmatched period");
                fail++;
            }
            else Console.WriteLine("PASS: still no DMC while unmatched");

            // 3) 终于匹配到 Waiting → 会话 Flush → 应入 DMC
            Console.WriteLine("[4] Waiting MATCHED now → session flush");
            if (!session.TryGetFolderToFlush(img.PeekFirstStagedFolder, out var fk) || fk != folder)
            {
                Console.WriteLine($"FAIL: expected session folder {folder}, got {fk}");
                fail++;
            }
            else Console.WriteLine($"  session folder={fk}");

            var n = img.FlushStagedToOutput(fk);
            Console.WriteLine($"[5] Flush → out={n} staged={img.StagedCount} dmc={cache.Count} enqueued={enqueued.Count}");
            if (n != 3)
            {
                Console.WriteLine("FAIL: expected flush 3");
                fail++;
            }
            else Console.WriteLine("PASS: flushed 3 after delayed Waiting");
            if (enqueued.Count != 3 || cache.Count != 3)
            {
                Console.WriteLine("FAIL: DMC should enqueue after delayed match");
                fail++;
            }
            else Console.WriteLine("PASS: DMC entered queue after 1-minute-class delayed Waiting match");
        }
        finally
        {
            try { img.Dispose(); } catch { /* */ }
            try { Directory.Delete(root, true); } catch { /* */ }
        }

        Console.WriteLine(fail == 0 ? "SIM_DELAYED_WAIT: PASS" : $"SIM_DELAYED_WAIT: FAIL count={fail}");
        Console.WriteLine("Conclusion: renamed/staged images survive long unmatched period; DMC enqueues only after Waiting flush.");
        return fail == 0 ? 0 : 1;
    }
}
