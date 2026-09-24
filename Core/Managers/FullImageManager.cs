using HalconDotNet;
using RBLAOI.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RBLAOI.Core.Managers
{
    /// <summary>
    /// 全图采集管理器 - 负责采集过程中的图片缓存、拼接和方案目录同步
    /// </summary>
    public class FullImageManager
    {
        private readonly List<string> _imagePathList = new List<string>();
        private readonly List<int> _imageGridIndices = new List<int>();   // 每个图片对应的网格索引 (row*cols+col)
        private readonly List<HObject> _cachedScaledImages = new List<HObject>();
        private HObject _currentStitchedImage = null;

        // 2026-08-25 优化 11：ReadImage 异步化后，缓存更新在后台线程执行——
        // _cacheLock 保护 _cachedScaledImages 的读(ConcatObj)/写(Dispose+赋值)互斥；
        // _tileVersions 每 GridIndex 版本号，保证连续模式下同一位的多次异步替换只允许最新结果写缓存（旧结果丢弃）。
        private readonly object _cacheLock = new object();
        private readonly Dictionary<int, long> _tileVersions = new Dictionary<int, long>();

        /// <summary>
        /// 当前已采集的图片数量
        /// </summary>
        public int ImageCount => _imagePathList.Count;

        /// <summary>
        /// 清除所有缓存（开始新采集前调用）
        /// </summary>
        public void Clear()
        {
            lock (_cacheLock)
            {
                foreach (var img in _cachedScaledImages)
                    img?.Dispose();
                _cachedScaledImages.Clear();
                _tileVersions.Clear();
            }

            _currentStitchedImage?.Dispose();
            _currentStitchedImage = null;

            _imagePathList.Clear();
            _imageGridIndices.Clear();
        }

        /// <summary>
        /// 添加一张新采集的图片
        /// </summary>
        public void AddImage(string filePath, int gridIndex)
        {
            _imagePathList.Add(filePath);
            _imageGridIndices.Add(gridIndex);
        }

        /// <summary>
        /// 获取最后添加的图片路径
        /// </summary>
        public string GetLastImagePath()
        {
            if (_imagePathList.Count == 0) return null;
            return _imagePathList.Last();
        }

        /// <summary>
        /// 批量加载图片路径（从方案 Images 目录加载时使用）
        /// </summary>
        public void LoadImages(IEnumerable<string> filePaths)
        {
            Clear();
            _imagePathList.AddRange(filePaths);
            for (int i = 0; i < _imagePathList.Count; i++)
                _imageGridIndices.Add(i);
        }

        /// <summary>
        /// 获取图片路径列表的副本
        /// </summary>
        public List<string> GetImagePaths()
        {
            return new List<string>(_imagePathList);
        }

        /// <summary>
        /// 拼接并显示图像
        /// </summary>
        public void StitchAndDisplay(Action<HObject> displayAction, int rows, int cols, double fovWidthMM, double fovHeightMM,
                   double overlap = 0, IProgress<int> progress = null)
        {
            if (_imagePathList.Count == 0) return;

            try
            {
                var (imgWidth, imgHeight, cropWidth, cropHeight) = CalculateTileDimensions(fovWidthMM, fovHeightMM, overlap, pixelsPerMm: 10);

                int count = _imagePathList.Count;
                int startIdx = _cachedScaledImages.Count;

                for (int i = startIdx; i < count; i++)
                {
                    string filePath = _imagePathList[i];
                    HObject image = new HObject();
                    try
                    {
                        HOperatorSet.ReadImage(out image, filePath);
                        HObject processed = ScaleAndCrop(image, imgWidth, imgHeight, cropWidth, cropHeight);
                        lock (_cacheLock)
                        {
                            _cachedScaledImages.Add(processed);
                        }
                    }
                    catch
                    {
                        image?.Dispose();
                        HObject empty = new HObject();
                        HOperatorSet.GenImageConst(out empty, "byte", cropWidth, cropHeight);
                        lock (_cacheLock)
                        {
                            _cachedScaledImages.Add(empty);
                        }
                    }
                    progress?.Report(i + 1);
                }

                RebuildStitchedAndDisplay(displayAction, rows, cols, cropWidth, cropHeight);
                Log.Debug($"拼接完成: {count} 张");
            }
            catch (Exception ex)
            {
                Log.Error($"拼接失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 替换指定网格位置的图片并刷新拼图显示（检测重新采集后使用）
        /// 2026-08-25 优化 11：ReadImage+ScaleAndCrop（19MB 大图解码+缩放）移入后台线程，
        /// 不再阻塞检测流程 A 段（实测 200-400ms/拍）。缓存写入由版本号保证最终一致：
        /// 连续模式下同 GridIndex 的多次替换，只有最新一次的结果能写缓存并触发重拼显示。
        /// </summary>
        public void ReplaceTile(int gridIndex, string newFilePath, Action<HObject> displayAction,
            int rows, int cols, double fovWidthMM, double fovHeightMM, double overlap = 0)
        {
            int idx = -1;
            for (int i = 0; i < _imageGridIndices.Count; i++)
            {
                if (_imageGridIndices[i] == gridIndex) { idx = i; break; }
            }
            if (idx < 0) return;

            _imagePathList[idx] = newFilePath;

            var (imgWidth, imgHeight, cropWidth, cropHeight) = CalculateTileDimensions(fovWidthMM, fovHeightMM, overlap, pixelsPerMm: 10);

            long ver;
            lock (_cacheLock)
            {
                _tileVersions.TryGetValue(idx, out long cur);
                ver = cur + 1;
                _tileVersions[idx] = ver;
            }

            // fire-and-forget：后台读图+缩放，完成后（若仍是最新版本）更新缓存并触发异步重拼显示
            _ = ReplaceTileCacheAsync(idx, newFilePath, ver, displayAction,
                rows, cols, imgWidth, imgHeight, cropWidth, cropHeight);
        }

        /// <summary>
        /// 后台瓦片缓存更新：ReadImage + ScaleAndCrop 在后台线程执行，不阻塞调用方（检测流程 A 段）。
        /// 版本号 ver 与 _tileVersions[idx] 不一致时结果作废（Dispose 释放），保证同 idx 只落最新图。
        /// </summary>
        private async Task ReplaceTileCacheAsync(int idx, string filePath, long ver, Action<HObject> displayAction,
            int rows, int cols, int imgWidth, int imgHeight, int cropWidth, int cropHeight)
        {
            HObject processed = await Task.Run(() =>
            {
                HObject image = new HObject();
                try
                {
                    HOperatorSet.ReadImage(out image, filePath);
                    HObject result = ScaleAndCrop(image, imgWidth, imgHeight, cropWidth, cropHeight);
                    image.Dispose();   // ScaleAndCrop 不接管原图（RGB 时 work=gray≠image，原图无人释放），19MB/张必须显式释放
                    return result;
                }
                catch (Exception ex)
                {
                    Log.Warning($"瓦片读图失败(异步): {filePath}: {ex.Message}");
                    image?.Dispose();
                    return null;
                }
            });

            if (processed == null) return;   // 读图失败：保留旧缓存图（比原同步版生成空图更优，显示不闪空）

            bool isLatest;
            lock (_cacheLock)
            {
                // idx < Count 防 Clear() 后（列表已清空）在途任务越界；版本号不一致判过期（Clear 清空版本字典 → 在途任务自动过期）
                isLatest = _tileVersions.TryGetValue(idx, out long cur) && cur == ver && idx < _cachedScaledImages.Count;
                if (isLatest)
                {
                    _cachedScaledImages[idx]?.Dispose();
                    _cachedScaledImages[idx] = processed;
                }
            }

            if (!isLatest)
            {
                processed.Dispose();   // 过期结果丢弃，不触发显示
                return;
            }

            // 缓存更新完成（最新版本）→ 异步重拼+显示（优化 5 既有逻辑）
            RebuildStitchedAsync(displayAction, rows, cols, cropWidth, cropHeight);
        }

        /// <summary>灰度化 → 缩放到显示尺寸 → 居中裁剪</summary>
        private HObject ScaleAndCrop(HObject image, int imgWidth, int imgHeight, int cropWidth, int cropHeight)
        {
            HTuple channels;
            HOperatorSet.CountChannels(image, out channels);
            HObject work = image;
            if (channels.I != 1)
            {
                HObject gray;
                HOperatorSet.Rgb1ToGray(image, out gray);
                work = gray;
            }

            HObject scaled = new HObject();
            HOperatorSet.ZoomImageSize(work, out scaled, imgWidth, imgHeight, "constant");
            if (work != image) work.Dispose();

            int cropOffX = (imgWidth - cropWidth) / 2;
            int cropOffY = (imgHeight - cropHeight) / 2;
            HObject cropped = new HObject();
            HOperatorSet.CropRectangle1(scaled, out cropped, cropOffY, cropOffX,
                cropOffY + cropHeight - 1, cropOffX + cropWidth - 1);
            scaled.Dispose();
            return cropped;
        }

        /// <summary>
        /// 从已缓存的缩放图重新构建拼图并显示（同步版，全图采集 StitchAndDisplay 使用）
        /// </summary>
        private void RebuildStitchedAndDisplay(Action<HObject> displayAction, int rows, int cols, int cropWidth, int cropHeight)
        {
            HObject stitched = BuildStitched(rows, cols, cropWidth, cropHeight);
            displayAction?.Invoke(stitched);
            _currentStitchedImage?.Dispose();
            _currentStitchedImage = stitched;
        }

        /// <summary>
        /// 异步重拼并显示（2026-08-24 优化 5，检测 ReplaceTile 使用）：
        /// 纯计算（ConcatObj+TileImages）在后台线程执行，完成后经 UI Dispatcher 先显示再替换 _currentStitchedImage。
        /// 调用方无需等待——下一处读 _currentStitchedImage 在 Merge 后（扫描期 ~4s 覆盖重拼 ~1s）。
        /// 竞态说明：_currentStitchedImage 的 Dispose/赋值/克隆（GetCurrentStitchedImage）全部收敛 UI 线程；
        /// _cachedScaledImages 的读写已由 _cacheLock 保护（优化 11 起写也在后台线程），BuildStitched 在锁内 ConcatObj。
        /// </summary>
        private void RebuildStitchedAsync(Action<HObject> displayAction, int rows, int cols, int cropWidth, int cropHeight)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)   // 无 UI 上下文（非 WPF 环境）：退回同步，保持原行为
            {
                RebuildStitchedAndDisplay(displayAction, rows, cols, cropWidth, cropHeight);
                return;
            }

            Task.Run(() =>
            {
                HObject stitched = null;
                try
                {
                    stitched = BuildStitched(rows, cols, cropWidth, cropHeight);
                }
                catch (Exception ex)
                {
                    Log.Error($"异步拼接失败: {ex.Message}");
                    return;
                }

                try
                {
                    dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            // 先显示成功再替换缓存（与原同步逻辑顺序一致；显示失败则保留旧图）
                            displayAction?.Invoke(stitched);
                            _currentStitchedImage?.Dispose();
                            _currentStitchedImage = stitched;
                        }
                        catch (Exception ex2)
                        {
                            // 显示失败不终止检测流程（原同步版会冒泡到流程 Err，异步后隔离为仅日志）
                            Log.Error($"拼接显示失败: {ex2.Message}");
                            try { stitched?.Dispose(); } catch { }
                        }
                    });
                }
                catch (Exception ex3)
                {
                    Log.Error($"拼接显示派发失败: {ex3.Message}");
                    try { stitched?.Dispose(); } catch { }
                }
            });
        }

        /// <summary>
        /// 纯计算：从已缓存缩放图构建拼接图（不碰 UI 与 _currentStitchedImage，可在后台线程执行）。
        /// 返回值所有权归调用方（调用方负责显示后 Dispose 或存入缓存）
        /// </summary>
        private HObject BuildStitched(int rows, int cols, int cropWidth, int cropHeight)
        {
            int totalSlots = rows * cols;
            Dictionary<int, int> slotMap = new Dictionary<int, int>();
            for (int i = 0; i < _imageGridIndices.Count; i++)
                slotMap[_imageGridIndices[i]] = i;

            HObject imagesTuple = new HObject();
            HOperatorSet.GenEmptyObj(out imagesTuple);
            try
            {
                for (int slot = 0; slot < totalSlots; slot++)
                {
                    // 优化 11：缓存更新已后台化，ConcatObj 必须在锁内完成（防与后台 Dispose+赋值并发）
                    lock (_cacheLock)
                    {
                        if (slotMap.TryGetValue(slot, out int cachedIdx) && cachedIdx < _cachedScaledImages.Count)
                        {
                            HObject temp = new HObject();
                            HOperatorSet.ConcatObj(imagesTuple, _cachedScaledImages[cachedIdx], out temp);
                            imagesTuple.Dispose();
                            imagesTuple = temp;
                            continue;
                        }
                    }

                    // 3 通道灰色占位图（与 ScaleAndCrop 输出的 RGB 格式一致）
                    HObject img = new HObject();
                    HOperatorSet.GenImageConst(out img, "byte", cropWidth, cropHeight);
                    HObject region = new HObject();
                    HOperatorSet.GenRectangle1(out region, 0, 0, cropHeight - 1, cropWidth - 1);
                    HOperatorSet.OverpaintRegion(img, region, 128, "fill");
                    region.Dispose();
                    HObject temp2 = new HObject();
                    HOperatorSet.ConcatObj(imagesTuple, img, out temp2);
                    imagesTuple.Dispose();
                    imagesTuple = temp2;
                    img.Dispose();
                }

                HObject stitched = new HObject();
                HOperatorSet.TileImages(imagesTuple, out stitched, cols, "horizontal");
                return stitched;
            }
            finally
            {
                imagesTuple.Dispose();
            }
        }

        /// <summary>
        /// 统一计算拼图瓦片尺寸（显示尺寸 + 裁剪后尺寸），内部和外部复用同一套公式
        /// </summary>
        public static (int displayW, int displayH, int cropW, int cropH) CalculateTileDimensions(
            double fovWidthMM, double fovHeightMM, double overlap, double pixelsPerMm = 10.0)
        {
            int displayW = (int)Math.Round(fovWidthMM * pixelsPerMm);
            int displayH = (int)Math.Round(fovHeightMM * pixelsPerMm);
            double overlapFactor = 1 - overlap;
            int cropW = (int)(displayW * overlapFactor);
            int cropH = (int)(displayH * overlapFactor);
            return (displayW, displayH, cropW, cropH);
        }

        /// <summary>
        /// 计算拼图显示的行列
        /// </summary>
        public (int, int) CalculateDisplayRowsCols(double boardWidth, double boardHeight, double fovWidth, double fovHeight, double overlap = 0)
        {
            double stepX = fovWidth * (1 - overlap);
            double stepY = fovHeight * (1 - overlap);

            int cols = (int)Math.Ceiling(boardWidth / stepX);
            int rows = (int)Math.Ceiling(boardHeight / stepY);

            return (rows, cols);
        }

        /// <summary>
        /// 清空当前方案的 Origin 图片文件夹
        /// </summary>
        public void ClearProjectImagesFolder()
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null) return;

            string projectDir = ProjectManager.Instance.CurrentProjectPath;
            if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir)) return;

            string originDir = ProjectManager.Instance.GetOriginPath();
            if (!Directory.Exists(originDir)) return;

            try
            {
                foreach (var file in Directory.GetFiles(originDir))
                    File.Delete(file);
                Log.Info("已清空方案图片目录: Origin");
            }
            catch (Exception ex)
            {
                Log.Error($"清空图片目录失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取当前拼接图像的克隆（供外部高亮等操作使用）
        /// </summary>
        public HObject GetCurrentStitchedImage()
        {
            if (_currentStitchedImage == null || !_currentStitchedImage.IsInitialized())
                return null;

            HObject clone = new HObject();
            HOperatorSet.CopyImage(_currentStitchedImage, out clone);
            return clone;
        }

        /// <summary>
        /// 获取当前拼接图像的原始引用（免拷贝，用于对性能敏感的显示场景）
        /// 注意：调用方不得 Dispose 返回的 HObject，否则会破坏缓存
        /// </summary>
        public HObject GetRawStitchedImage()
        {
            if (_currentStitchedImage == null || !_currentStitchedImage.IsInitialized())
                return null;
            return _currentStitchedImage;
        }

        /// <summary>
        /// 设置网格索引映射（加载已保存的映射时使用）
        /// </summary>
        public void SetGridIndices(List<int> indices)
        {
            _imageGridIndices.Clear();
            _imageGridIndices.AddRange(indices);
        }

        /// <summary>
        /// 获取网格索引映射的副本
        /// </summary>
        public List<int> GetGridIndices()
        {
            return new List<int>(_imageGridIndices);
        }

        /// <summary>
        /// 替换最后一张图片的路径（用于同盘重命名后更新引用）
        /// </summary>
        public void ReplaceLastImagePath(string newPath)
        {
            if (_imagePathList.Count > 0)
                _imagePathList[_imagePathList.Count - 1] = newPath;
        }

        public void Dispose()
        {
            Clear();
        }
    }
}
