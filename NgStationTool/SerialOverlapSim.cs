using NgStationTool.Services;

namespace NgStationTool;

/// <summary>
/// 仿真：组A 5张 NG 未判完时组B 3张又进 → 验证串行会话不会一起 Out/入队。
/// 运行：NgStationTool.exe --sim-serial
/// </summary>
internal static class SerialOverlapSim
{
    public static int Run()
    {
        Console.WriteLine("=== Serial overlap simulation (5 then 3 while first not done) ===");
        var root = Path.Combine(Path.GetTempPath(), "ng-serial-sim-" + Guid.NewGuid().ToString("N")[..8]);
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
            SkipIfSameSizeExists = false
        };
        AppConfig live = cfg;

        var log = new AppLogger(Path.Combine(root, "sim_log.txt"), 500);
        var cache = new DmcPendingCache(log, 120);
        var session = new JudgmentSessionCoordinator(log, () => live);
        var enqueued = new List<(string dmc, string folder)>();
        var img = new ImageCopyWatcher(log, () => live, (stem, path, folder) =>
        {
            enqueued.Add((stem, folder));
            cache.TryEnqueue(stem, "sim", path, folder);
            Console.WriteLine($"  [入DMC] {stem} 组={folder}");
        });

        var jpeg = new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
            0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
        };
        var pad = new byte[64];
        Array.Copy(jpeg, pad, jpeg.Length);

        string MakeSrc(string folder, int i)
        {
            var dir = Path.Combine(watch, folder);
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, $"img{i}.jpg");
            File.WriteAllBytes(p, pad);
            return p;
        }

        int fail = 0;
        try
        {
            // --- 组A：5 张暂存 ---
            const string A = "PieceA_NG";
            const string B = "PieceB_NG";
            for (var i = 1; i <= 5; i++)
            {
                var src = MakeSrc(A, i);
                img.EnqueueStagedForTest(A, src, $"{A}_img{i}.jpg");
            }
            Console.WriteLine($"[1] 组A 已暂存 5 张，Staged={img.StagedCount}");

            // Waiting：应只开 A 会话并 Out 5
            if (!session.TryGetFolderToFlush(img.PeekFirstStagedFolder, out var f1, out _) || f1 != A)
            {
                Console.WriteLine($"FAIL: 期望会话 A，得到 {f1}");
                fail++;
            }
            else Console.WriteLine($"[2] Waiting → 会话开始 组={f1}");

            var n1 = img.FlushStagedToOutput(f1);
            Console.WriteLine($"[3] Flush 组A → 输出 {n1} 张，剩余暂存={img.StagedCount}，DMC缓存={cache.Count}");
            if (n1 != 5) { Console.WriteLine("FAIL: 组A应输出5"); fail++; }
            else Console.WriteLine("PASS: 组A 5 张已 Out/入队");
            if (img.StagedCount != 0) { Console.WriteLine("FAIL: 此时暂存应空"); fail++; }

            // --- 组B：3 张在 A 未结束时进入 ---
            for (var i = 1; i <= 3; i++)
            {
                var src = MakeSrc(B, i);
                img.EnqueueStagedForTest(B, src, $"{B}_img{i}.jpg");
            }
            Console.WriteLine($"[4] 组A 仍在判，组B 又暂存 3 张，Staged={img.StagedCount}");

            // 持续 Waiting：仍应只 Flush A（0张），B 留下
            if (!session.TryGetFolderToFlush(img.PeekFirstStagedFolder, out var f2, out _) || f2 != A)
            {
                Console.WriteLine($"FAIL: 持续Waiting仍应绑定A，得到 {f2}");
                fail++;
            }
            else Console.WriteLine($"[5] 持续 Waiting → 仍会话 组={f2}（不接 B）");

            var n2 = img.FlushStagedToOutput(f2);
            Console.WriteLine($"[6] Flush(A) → 输出 {n2}（期望0），剩余暂存={img.StagedCount}（期望3）");
            if (n2 != 0) { Console.WriteLine("FAIL: 不应再输出A"); fail++; }
            if (img.StagedCount != 3) { Console.WriteLine("FAIL: B的3张应仍暂存"); fail++; }
            else Console.WriteLine("PASS: 组B 3 张仍排队，未与A一起判定");

            var enqB = enqueued.Count(x => x.folder.Equals(B, StringComparison.OrdinalIgnoreCase));
            if (enqB != 0)
            {
                Console.WriteLine($"FAIL: 组B不应已入DMC，实际={enqB}");
                fail++;
            }
            else Console.WriteLine("PASS: 组B 尚未入 DMC 缓存");

            // --- 组A 整组结束 + 仍 Waiting：不能开B ---
            session.NotifyFolderGroupFinished(A, "OK+Enter", uiStillWaiting: true);
            if (session.TryGetFolderToFlush(img.PeekFirstStagedFolder, out _, out _))
            {
                Console.WriteLine("FAIL: 等离开Waiting期间不应Flush B");
                fail++;
            }
            else Console.WriteLine("PASS: 组A结束后界面仍Waiting → 不接B");

            // --- 离开 Waiting + 500ms 延迟 ---
            session.OnHaranMatchKind(HaranUiMatchService.MatchKind.Unknown);
            if (session.TryGetFolderToFlush(img.PeekFirstStagedFolder, out _, out _))
            {
                Console.WriteLine("FAIL: 冷却期内不应开B");
                fail++;
            }
            else Console.WriteLine("PASS: 离开Waiting后进入冷却，暂不接B");

            Console.WriteLine("[7] 模拟冷却 500ms…");
            Thread.Sleep(520);

            if (!session.TryGetFolderToFlush(img.PeekFirstStagedFolder, out var f3, out _) || f3 != B)
            {
                Console.WriteLine($"FAIL: 冷却后应开会话B，得到 {f3}");
                fail++;
            }
            else Console.WriteLine($"[8] 冷却结束 + Waiting → 会话 组={f3}");

            var n3 = img.FlushStagedToOutput(f3);
            Console.WriteLine($"[9] Flush 组B → 输出 {n3} 张，剩余暂存={img.StagedCount}，DMC={cache.Count}");
            if (n3 != 3) { Console.WriteLine("FAIL: 组B应输出3"); fail++; }
            else Console.WriteLine("PASS: 组B 3 张在A结束后才判定");

            enqB = enqueued.Count(x => x.folder.Equals(B, StringComparison.OrdinalIgnoreCase));
            var enqA = enqueued.Count(x => x.folder.Equals(A, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"[汇总] 入队 A={enqA} B={enqB} 总计={enqueued.Count}");
            if (enqA != 5 || enqB != 3)
            {
                Console.WriteLine("FAIL: 入队数量不对");
                fail++;
            }
            else Console.WriteLine("PASS: 入队顺序/数量正确（先5后3，不混冲）");
        }
        finally
        {
            try { img.Dispose(); } catch { /* */ }
            try { Directory.Delete(root, true); } catch { /* */ }
        }

        Console.WriteLine(fail == 0 ? "SIM_SERIAL: PASS" : $"SIM_SERIAL: FAIL count={fail}");
        Console.WriteLine();
        Console.WriteLine("结论：前组未结束时后组只进暂存排队；等前组结束 + 离开Waiting + 延迟后，后组才 Out/入DMC。");
        return fail == 0 ? 0 : 1;
    }
}
