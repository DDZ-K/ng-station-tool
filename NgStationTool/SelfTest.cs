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
        var judging = Path.Combine(root, "judging");
        var reports = Path.Combine(root, "reports");
        var reportArchive = Path.Combine(root, "report_archive");
        var logs = Path.Combine(root, "logs");
        var archive = Path.Combine(root, "logs_done");
        Directory.CreateDirectory(watch);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(judging);
        Directory.CreateDirectory(reports);
        Directory.CreateDirectory(reportArchive);
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(archive);

        var cfgPath = Path.Combine(root, "config.json");
        var cfg = new AppConfig
        {
            EnableImageCopy = true,
            EnableCloudRelease = true,
            WatchRoot = watch,
            OutputRoot = output,
            JudgingRoot = judging,
            XmlWatchRoot = reports,
            XmlArchiveRoot = reportArchive,
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
            EnqueueFromImageCopyFolderName = false, // v1.5.1：即使 false，拷贝后也必须入待NG（回归本 bug）
            EnqueueFromNgImageWatch = false, // 本测只测文件夹名入队，避免输出目录二次入队干扰
            LogReadyBudgetMs = 500,
            ResultLineNumber = 1, // 废弃字段，忽略
            OkTokens = new List<string> { "OK" },
            NokTokens = new List<string> { "NOK" },
            OkKey = "NumPad9",
            NokKey = "NumPad7",
            AutoStartOnLaunch = false
        };
        cfg.Save(cfgPath);

        var log = new AppLogger(Path.Combine(root, "test_log.txt"), 200);
        var cache = new DmcPendingCache(log);
        var ngQueue = new NgPendingQueue(log);
        var kb = new KeyboardService(log) { DryRun = true };
        AppConfig live = cfg;
        var cloud = new CloudReleaseService(log, () => live, cache, kb);
        var xmlGate = new XmlDmcGateService(log, () => live, ngQueue, (name, path, productDmc) =>
            cloud.EnqueueDmc(name, "XmlMatched", path, productDmc));
        var img = new ImageCopyWatcher(log, () => live, (renamed, path, folder) =>
        {
            ngQueue.Enqueue(renamed, folder, path);
        });

        var fail = 0;
        try
        {
            // UI 生命周期契约：普通关闭只收起托盘；托盘“退出”才真正结束。
            if (!TrayClosePolicy.ShouldMinimizeToTray(CloseReason.UserClosing, explicitExitRequested: false)
                || !TrayClosePolicy.ShouldMinimizeToTray(CloseReason.None, explicitExitRequested: false)
                || TrayClosePolicy.ShouldMinimizeToTray(CloseReason.UserClosing, explicitExitRequested: true))
            {
                Console.WriteLine("FAIL: tray close policy");
                fail++;
            }
            else Console.WriteLine("PASS: tray close policy");

            // 料号白名单：产品 DMC 包含任一维护料号才服务；空列表/仅空白=不过滤（兼容旧配置）
            var parts = new[] { "6915300000", "1234567890" };
            if (!PartNumberRules.IsServedProduct("69153000000110002204088638907524", parts)
                || !PartNumberRules.IsServedProduct("XX1234567890YY", parts)
                || PartNumberRules.IsServedProduct("99999999999999", parts)
                || !PartNumberRules.IsServedProduct("ANYTHING", Array.Empty<string>())
                || !PartNumberRules.IsServedProduct("ANYTHING", null)
                || !PartNumberRules.IsServedProduct("6915300000011", new[] { "  ", "" }) // 空白项被忽略 → 等同空列表
                || !PartNumberRules.TryFindMatchedPart("ABC6915300000XYZ", parts, out var hit)
                || hit != "6915300000")
            {
                Console.WriteLine("FAIL: part-number match rules");
                fail++;
            }
            else Console.WriteLine("PASS: part-number match rules");

            // 队列清空 API（对应主界面两个「清空队列」按钮）
            ngQueue.Enqueue("CLEAR_IMG1", "CLEAR_PROD", Path.Combine(output, "CLEAR_IMG1.jpg"));
            ngQueue.Enqueue("CLEAR_IMG2", "CLEAR_PROD", Path.Combine(output, "CLEAR_IMG2.jpg"));
            cache.TryEnqueue("CLEAR_JUDGE1", "selftest", null, folderKey: "CLEAR_PROD");
            if (ngQueue.Count < 2 || cache.Count < 1)
            {
                Console.WriteLine("FAIL: seed queues before clear");
                fail++;
            }
            else
            {
                ngQueue.ClearAll("selftest-clear-ng");
                cache.ClearAll("selftest-clear-judging");
                if (ngQueue.Count != 0 || cache.Count != 0)
                {
                    Console.WriteLine($"FAIL: clear queues ng={ngQueue.Count} judging={cache.Count}");
                    fail++;
                }
                else Console.WriteLine("PASS: clear NG + judging queues");
            }

            img.Start();
            cloud.Start();
            xmlGate.Start();
            Thread.Sleep(800);

            // 料号过滤：未命中产品不进 A / 待NG；命中则正常入队
            {
                var jpeg = new byte[256];
                var jpegHead = new byte[]
                {
                    0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
                    0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00
                };
                Array.Copy(jpegHead, jpeg, jpegHead.Length);
                jpeg[^2] = 0xFF; jpeg[^1] = 0xD9;
                live.PartNumbers = new List<string> { "SERVEME10XX" };
                // 确保规则本身对文件夹名成立（避免只是时序误报）
                if (!PartNumberRules.IsServedProduct("PRE_SERVEME10XX_TAIL", live.PartNumbers)
                    || PartNumberRules.IsServedProduct("OTHERPRODUCT999", live.PartNumbers))
                {
                    Console.WriteLine("FAIL: part-number rules vs integration folders");
                    fail++;
                }

                var rejectFolder = "OTHERPRODUCT999";
                var rejectSub = Path.Combine(watch, rejectFolder);
                Directory.CreateDirectory(rejectSub);
                Thread.Sleep(150);
                File.WriteAllBytes(Path.Combine(rejectSub, "x.jpg"), jpeg);
                Thread.Sleep(Math.Max(1500, cfg.FolderSettleMs + 900));
                var rejectOut = Directory.Exists(output)
                    ? Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                        .Any(f => Path.GetFileName(f).StartsWith(rejectFolder, StringComparison.OrdinalIgnoreCase))
                    : false;
                if (ngQueue.Snapshot().Any(x => x.ProductDmc.Equals(rejectFolder, StringComparison.OrdinalIgnoreCase)) || rejectOut)
                {
                    Console.WriteLine("FAIL: non-matching part-number product should be ignored");
                    fail++;
                }
                else Console.WriteLine("PASS: non-matching part-number product ignored");

                var acceptFolder = "PRE_SERVEME10XX_TAIL";
                var acceptSub = Path.Combine(watch, acceptFolder);
                Directory.CreateDirectory(acceptSub);
                Thread.Sleep(200);
                var acceptJpg = Path.Combine(acceptSub, "y.jpg");
                File.WriteAllBytes(acceptJpg, jpeg);
                // 再触一次写，保证 FSW 与静默计时都能看到
                Thread.Sleep(100);
                File.SetLastWriteTimeUtc(acceptJpg, DateTime.UtcNow);
                File.WriteAllBytes(acceptJpg, jpeg);

                var acceptDeadline = DateTime.Now.AddSeconds(15);
                while (DateTime.Now < acceptDeadline
                       && !ngQueue.Snapshot().Any(x => x.ProductDmc.Equals(acceptFolder, StringComparison.OrdinalIgnoreCase)))
                    Thread.Sleep(100);
                if (!ngQueue.Snapshot().Any(x => x.ProductDmc.Equals(acceptFolder, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine("FAIL: matching part-number product should enter pending-NG; queue=" +
                        string.Join(",", ngQueue.Snapshot().Select(x => x.ProductDmc + ":" + x.ImageName)));
                    fail++;
                }
                else Console.WriteLine("PASS: matching part-number product entered pending-NG");

                ngQueue.ClearAll("selftest-part-filter-cleanup");
                live.PartNumbers = new List<string>(); // 后续用例保持不过滤
            }

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

            var expected1 = dmc + "_cam" + (cfg.AppendDateToFileName ? "_" + DateTime.Now.ToString(cfg.FileNameDateFormat) : "");
            var expected2 = dmc + "_cam2" + (cfg.AppendDateToFileName ? "_" + DateTime.Now.ToString(cfg.FileNameDateFormat) : "");
            var deadline = DateTime.Now.AddSeconds(12);
            var copied = 0;
            while (DateTime.Now < deadline)
            {
                if (Directory.Exists(output))
                    copied = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                        .Count(f => Path.GetFileName(f).StartsWith(dmc + "_", StringComparison.OrdinalIgnoreCase));
                if (copied >= 2 && ngQueue.Count == 2) break;
                Thread.Sleep(100);
            }

            if (copied < 2)
            {
                Console.WriteLine("FAIL: batch copy expected 2 got " + copied);
                fail++;
            }
            else Console.WriteLine("PASS: batch copied 2 images");

            if (!ngQueue.Snapshot().Any(x => x.ImageName == expected1)
                || !ngQueue.Snapshot().Any(x => x.ImageName == expected2))
            {
                Console.WriteLine("FAIL: expected 2 pending-NG images, queue=" +
                    string.Join(",", ngQueue.Snapshot().Select(x => x.ImageName)));
                fail++;
            }
            else Console.WriteLine("PASS: two renamed images in pending-NG queue");

            // XML identifier 命中产品文件夹名：A 中整组图片移到 B/年/月/日，并转入待判断队列
            var matchedXml = Path.Combine(reports, "matched.xml");
            File.WriteAllText(matchedXml,
                $"<?xml version=\"1.0\"?><root><event><partReceived identifier=\"{dmc}\" /></event></root>");
            deadline = DateTime.Now.AddSeconds(8);
            while (DateTime.Now < deadline && (ngQueue.Count != 0 || cache.Count != 2)) Thread.Sleep(100);
            var judgingFiles = Directory.Exists(judging)
                ? Directory.EnumerateFiles(judging, "*", SearchOption.AllDirectories).ToList()
                : new List<string>();
            if (ngQueue.Count != 0 || !cache.Contains(expected1) || !cache.Contains(expected2)
                || judgingFiles.Count != 2 || File.Exists(matchedXml))
            {
                Console.WriteLine($"FAIL: XML matched gate ng={ngQueue.Count} judging={cache.Count} filesB={judgingFiles.Count}");
                fail++;
            }
            else Console.WriteLine("PASS: XML identifier moved A to dated B and promoted queue");

            var unmatchedXml = Path.Combine(reports, "unmatched.xml");
            File.WriteAllText(unmatchedXml,
                "<?xml version=\"1.0\"?><root><event><partReceived identifier=\"NOT_IN_QUEUE\" /></event></root>");
            deadline = DateTime.Now.AddSeconds(5);
            while (DateTime.Now < deadline && File.Exists(unmatchedXml)) Thread.Sleep(100);
            var archivedMatched = Directory.EnumerateFiles(reportArchive, "matched.xml", SearchOption.AllDirectories).Any();
            var archivedUnmatched = Directory.EnumerateFiles(reportArchive, "unmatched.xml", SearchOption.AllDirectories).Any();
            if (!archivedMatched || !archivedUnmatched)
            {
                Console.WriteLine("FAIL: XML reports must archive into matched/unmatched folders");
                fail++;
            }
            else Console.WriteLine("PASS: matched and unmatched XML reports archived");

            // 1a) 产品文件夹带站位后缀 _S1，XML identifier 为无后缀主体 → 应命中
            {
                var baseId = "DMCSUFFIX001";
                var folderSuf = baseId + "_S1";
                var subSuf = Path.Combine(watch, folderSuf);
                Directory.CreateDirectory(subSuf);
                Thread.Sleep(150);
                var jpgSuf = Path.Combine(subSuf, "view.jpg");
                File.WriteAllBytes(jpgSuf, buf);
                var expectedSuf = folderSuf + "_view" + (cfg.AppendDateToFileName ? "_" + DateTime.Now.ToString(cfg.FileNameDateFormat) : "");
                deadline = DateTime.Now.AddSeconds(12);
                while (DateTime.Now < deadline && !ngQueue.Snapshot().Any(x => x.ImageName == expectedSuf)) Thread.Sleep(100);
                if (!ngQueue.Snapshot().Any(x => x.ImageName == expectedSuf && x.ProductDmc == folderSuf))
                {
                    Console.WriteLine("FAIL: suffix folder not enqueued to pending-NG");
                    fail++;
                }
                else Console.WriteLine("PASS: suffix product folder enqueued to pending-NG");

                var xmlSuf = Path.Combine(reports, "suffix.xml");
                File.WriteAllText(xmlSuf,
                    $"<?xml version=\"1.0\"?><root><event><partReceived identifier=\"{baseId}\" /></event></root>");
                deadline = DateTime.Now.AddSeconds(8);
                while (DateTime.Now < deadline && ngQueue.Snapshot().Any(x => x.ImageName == expectedSuf)) Thread.Sleep(100);
                if (ngQueue.Snapshot().Any(x => x.ImageName == expectedSuf) || !cache.Contains(expectedSuf))
                {
                    Console.WriteLine($"FAIL: identifier without _S1 should promote suffix folder; ngHas={ngQueue.Snapshot().Any(x => x.ImageName == expectedSuf)} judgingHas={cache.Contains(expectedSuf)}");
                    fail++;
                }
                else Console.WriteLine("PASS: identifier matched product folder with _S1 suffix");
                cache.ForceRemove(expectedSuf, "selftest suffix cleanup");
            }

            // 1b) 单张文件夹：只 1 张图也必须拷贝+入队（回归：单张不行）
            cache.ForceRemove(expected1, "selftest single prep");
            cache.ForceRemove(expected2, "selftest single prep");
            var dmcSolo = "DMCTEST_SOLO";
            var subSolo = Path.Combine(watch, dmcSolo);
            Directory.CreateDirectory(subSolo);
            Thread.Sleep(150);
            var soloJpg = Path.Combine(subSolo, "only.jpg");
            File.WriteAllBytes(soloJpg, buf);
            var expectedSolo = dmcSolo + "_only" + (cfg.AppendDateToFileName ? "_" + DateTime.Now.ToString(cfg.FileNameDateFormat) : "");
            deadline = DateTime.Now.AddSeconds(12);
            var soloOk = false;
            while (DateTime.Now < deadline)
            {
                if (ngQueue.Snapshot().Any(x => x.ImageName == expectedSolo)
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
                    string.Join(",", ngQueue.Snapshot().Select(x => x.ImageName)));
                fail++;
            }
            else Console.WriteLine("PASS: single-image folder enqueued");

            // 1c) 同大小已存在时：重写源图时间戳后仍应再入队（不要求再拷一份）
            ngQueue.Remove(expectedSolo);
            Thread.Sleep(200);
            // 覆盖写入 → LastWriteTime 变新
            File.WriteAllBytes(soloJpg, buf);
            File.SetLastWriteTimeUtc(soloJpg, DateTime.UtcNow.AddSeconds(1));
            deadline = DateTime.Now.AddSeconds(12);
            var requeueOk = false;
            while (DateTime.Now < deadline)
            {
                if (ngQueue.Snapshot().Any(x => x.ImageName == expectedSolo)) { requeueOk = true; break; }
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
            ngQueue.Remove(expectedSolo);
            Thread.Sleep(200);
            var orphanLog = Path.Combine(logs, expected1 + ".log");
            File.WriteAllText(orphanLog, "ERER\nResult=OK\n");
            Thread.Sleep(1500);
            if (cache.Contains(expected1))
            {
                Console.WriteLine("FAIL: orphan log must not create cache");
                fail++;
            }
            else Console.WriteLine("PASS: orphan log ignored (no cache)");

            // 3) 有缓存 + log（前缀+DMC）+ 全文 Result=OK → OK 键 + 单条组立刻回车
            var dmc2 = "DMCTEST002_cam";
            cache.TryEnqueue(dmc2, "selftest", jpg, folderKey: "solo");
            var logPath2 = Path.Combine(logs, "prefix_" + dmc2 + ".txt");
            File.WriteAllText(logPath2, "ERER\nResult=OK\n");

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

            // 3b) Result=NOK 必须判 NOK（不能因含 OK 子串而当 OK）
            var dmcNok = "DMCTEST_NOK_cam";
            cache.TryEnqueue(dmcNok, "selftest", null, folderKey: "solo_nok");
            var logNok = Path.Combine(logs, "prefix_" + dmcNok + ".txt");
            File.WriteAllText(logNok, "ERER\nResult=NOK\n");
            deadline = DateTime.Now.AddSeconds(6);
            var nokLeft = true;
            while (DateTime.Now < deadline)
            {
                if (!cache.Contains(dmcNok)) { nokLeft = false; break; }
                Thread.Sleep(100);
            }
            if (nokLeft)
            {
                Console.WriteLine("FAIL: Result=NOK must dequeue DMC (not confuse with OK substring)");
                fail++;
            }
            else Console.WriteLine("PASS: Result=NOK judged without OK false-positive");

            // 4) 同文件夹两组：先判一条不应清完组；两条都判完才结束
            cache.ClearAll("selftest-before-folder");
            cache.TryEnqueue("FOLDER_IMG1", "selftest", null, folderKey: "FOLDER");
            cache.TryEnqueue("FOLDER_IMG2", "selftest", null, folderKey: "FOLDER");
            File.WriteAllText(Path.Combine(logs, "p_FOLDER_IMG1.txt"), "ERER\nResult=OK\n");
            deadline = DateTime.Now.AddSeconds(10);
            while (DateTime.Now < deadline && cache.Contains("FOLDER_IMG1")) Thread.Sleep(100);
            if (cache.Contains("FOLDER_IMG1") || !cache.Contains("FOLDER_IMG2") || cache.CountInFolder("FOLDER") != 1)
            {
                Console.WriteLine("FAIL: folder batch partial: " + string.Join(",", cache.Snapshot().Select(x => x.Dmc)));
                fail++;
            }
            else Console.WriteLine("PASS: first of folder judged, second still pending");

            File.WriteAllText(Path.Combine(logs, "p_FOLDER_IMG2.txt"), "ERER\nResult=OK\n");
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

        }
        finally
        {
            try { img.Stop(); xmlGate.Stop(); cloud.Stop(); } catch { /* ignore */ }
            try { img.Dispose(); xmlGate.Dispose(); cloud.Dispose(); } catch { /* ignore */ }
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }

        Console.WriteLine(fail == 0 ? "SELF_TEST: PASS" : $"SELF_TEST: FAIL count={fail}");
        return fail == 0 ? 0 : 1;
    }
}
