﻿using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using Compunet.YoloV8;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Model.Area;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using static Vanara.PInvoke.Gdi32;
using System.Drawing;
using BetterGenshinImpact.GameTask.AutoPathing;

namespace BetterGenshinImpact.GameTask.AutoFight;

public class AutoFightTask : ISoloTask
{
    private readonly AutoFightParam _taskParam;

    private readonly CombatScriptBag _combatScriptBag;

    private CancellationTokenSource? _cts;

    private readonly BgiYoloV8Predictor _predictor;

    private DateTime _lastFightFlagTime = DateTime.Now; // 战斗标志最近一次出现的时间

    public AutoFightTask(AutoFightParam taskParam)
    {
        _taskParam = taskParam;
        _combatScriptBag = CombatScriptParser.ReadAndParse(_taskParam.CombatStrategyPath);

        if (_taskParam.EndDetect || _taskParam.AutoPickAfterFight)
        {
            _predictor = new BgiYoloV8Predictor(@"Assets\Model\World\bgi_world.onnx");
        }
    }

    public async Task Start(CancellationTokenSource cts)
    {
        _cts = cts;

        LogScreenResolution();
        var combatScenes = new CombatScenes().InitializeTeam(CaptureToRectArea());
        if (!combatScenes.CheckTeamInitialized())
        {
            throw new Exception("识别队伍角色失败");
        }

        var combatCommands = _combatScriptBag.FindCombatScript(combatScenes.Avatars);

        // 新的取消token
        var cts2 = new CancellationTokenSource();
        cts2.Token.Register(cts.Cancel);

        combatScenes.BeforeTask(cts2);

        // 战斗操作
        var fightTask = Task.Run(() =>
        {
            try
            {
                while (!cts2.Token.IsCancellationRequested)
                {
                    // 通用化战斗策略
                    foreach (var command in combatCommands)
                    {
                        command.Execute(combatScenes);
                    }
                }
            }
            catch
            {
            }
        }, cts2.Token);

        // 战斗结束检测线程
        var endTask = Task.Run(async () =>
        {
            if (!_taskParam.EndDetect)
            {
                return;
            }

            try
            {
                while (!cts2.IsCancellationRequested)
                {
                    var finish = await CheckFightFinish();
                    if (finish)
                    {
                        await cts2.CancelAsync();
                        break;
                    }

                    Sleep(1000, cts2);
                }
            }
            catch
            {
            }
        }, cts2.Token);

        await Task.WhenAll(fightTask, endTask);

        if (_taskParam.AutoPickAfterFight)
        {
            // 执行自动拾取掉落物的功能
        }
    }

    private void LogScreenResolution()
    {
        var gameScreenSize = SystemControl.GetGameScreenRect(TaskContext.Instance().GameHandle);
        if (gameScreenSize.Width * 9 != gameScreenSize.Height * 16)
        {
            Logger.LogWarning("游戏窗口分辨率不是 16:9 ！当前分辨率为 {Width}x{Height} , 非 16:9 分辨率的游戏可能无法正常使用自动战斗功能 !", gameScreenSize.Width, gameScreenSize.Height);
        }
    }

    private async Task<bool> CheckFightFinish()
    {
        //  YOLO 判断血条和怪物位置
        if (HasFightFlag(CaptureToRectArea()))
        {
            _lastFightFlagTime = DateTime.Now;
            return false;
        }

        // 几秒内没有检测到血条和怪物位置，则开始旋转视角重新检测
        if ((DateTime.Now - _lastFightFlagTime).TotalSeconds > 5)
        {
            // 旋转完毕后都没有检测到血条和怪物位置，则按L键确认战斗结束
            List<int> angles = [0, 90, 180, 270];
            var rotateTask = new CameraRotateTask(_cts!);
            foreach (var a in angles)
            {
                await rotateTask.WaitUntilRotatedTo(a, 5);
                await Delay(1000, _cts!); // 等待视角稳定
                if (HasFightFlag(CaptureToRectArea()))
                {
                    return false;
                }
            }

            // 最终方案确认战斗结束
            Logger.LogInformation("识别到战斗结束");
            return true;
        }

        return false;
    }

    private bool HasFightFlag(ImageRegion imageRegion)
    {
        var dict = _predictor.Detect(imageRegion);
        return dict.ContainsKey("health_bar") || dict.ContainsKey("enemy_identify");
    }
}
