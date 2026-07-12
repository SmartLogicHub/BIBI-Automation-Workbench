using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.Application;
using Ray.BiliBiliTool.Application.Contracts;
using Ray.BiliBiliTool.Application.Contracts.ContentAutomation;
using Ray.BiliBiliTool.Application.Contracts.MaintenanceWorkflows;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService.Dtos;
using Ray.BiliBiliTool.DomainService.Interfaces;
using Ray.BiliBiliTool.Infrastructure.Cookie;

namespace AppServiceTest;

public class MaintenanceWorkflowServiceTest
{
    [Fact]
    public async Task GetNodeCatalogAsync_ShouldOnlyExposeBusinessReadyNodes()
    {
        var service = new MaintenanceWorkflowService();

        var nodes = await service.GetNodeCatalogAsync();

        Assert.All(nodes, node => Assert.True(node.IsExecutable, node.DisplayName));
        Assert.DoesNotContain(
            nodes,
            node => node.DisplayName.Contains("完整执行", StringComparison.OrdinalIgnoreCase)
        );
        Assert.DoesNotContain(
            nodes,
            node => node.Description.Contains("Service", StringComparison.OrdinalIgnoreCase)
        );
        Assert.DoesNotContain(
            nodes,
            node => node.Description.Contains("BiliBiliToolPro", StringComparison.OrdinalIgnoreCase)
        );
        Assert.DoesNotContain(
            nodes,
            node => node.Description.Contains("待抽取", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task GetNodeCatalogAsync_ShouldExposeRealBiliSubTaskNodesWithPermissionMetadata()
    {
        var service = new MaintenanceWorkflowService();

        var nodes = await service.GetNodeCatalogAsync();

        Assert.Contains(
            nodes,
            x => x.NodeType == "daily.watch_share" && x.Group == "随机互动" && x.IsExecutable
        );
        Assert.Contains(
            nodes,
            x => x.NodeType == "social.random_follow" && x.Group == "随机互动" && x.IsExecutable
        );
        Assert.Contains(
            nodes,
            x => x.NodeType == "task.vip_big_point" && x.Permission == "big_vip" && x.IsExecutable
        );
        Assert.Contains(
            nodes,
            x =>
                x.NodeType == "live_fans_medal.heartbeat" && x.Group == "直播互动" && x.IsExecutable
        );
        Assert.DoesNotContain(nodes, x => x.NodeType == "control.manual_confirm");
        Assert.DoesNotContain(nodes, x => string.IsNullOrWhiteSpace(x.DisplayName));
        Assert.DoesNotContain(
            nodes,
            x => x.NodeType.StartsWith("account.", StringComparison.OrdinalIgnoreCase)
        );
        Assert.DoesNotContain(nodes, x => x.NodeType == "task.comment_automation");
        Assert.DoesNotContain(nodes, x => x.Group == "账号基础");
    }

    [Fact]
    public async Task GetWorkflowsAsync_ShouldCreateASafeRelevantDefaultFlow()
    {
        var service = new MaintenanceWorkflowService();

        var workflow = Assert.Single(await service.GetWorkflowsAsync());

        Assert.Equal("默认养号流程", workflow.Name);
        Assert.Equal(string.Empty, workflow.TimeWindow);
        Assert.Equal(
            ["task.daily", "control.wait", "daily.random_watch", "control.log_result"],
            workflow.Steps.OrderBy(step => step.SortOrder).Select(step => step.NodeType).ToArray()
        );
        Assert.DoesNotContain(workflow.Steps, step => step.NodeType == "task.manga");
    }

    [Fact]
    public async Task SaveWorkflowAsync_ShouldNormalizeWorkflowAndPersistStepOrder()
    {
        var service = new MaintenanceWorkflowService();

        var saved = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "日常维护",
                AccountIds = ["  account-1  ", ""],
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "daily.watch_share",
                        DisplayName = "观看分享",
                        SortOrder = 20,
                    },
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.wait",
                        DisplayName = "等待",
                        SortOrder = 10,
                        IntervalSeconds = 30,
                    },
                ],
            }
        );

        Assert.False(string.IsNullOrWhiteSpace(saved.Id));
        Assert.Equal("日常维护", saved.Name);
        Assert.Equal(["account-1"], saved.AccountIds);
        Assert.Equal(
            ["control.wait", "daily.watch_share"],
            saved.Steps.Select(x => x.NodeType).ToArray()
        );
        Assert.All(saved.Steps, x => Assert.False(string.IsNullOrWhiteSpace(x.Id)));
    }

    [Fact]
    public async Task SaveWorkflowAsync_ShouldKeepRunHistoryAlignedWithCurrentName()
    {
        var service = new MaintenanceWorkflowService();
        var workflow = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "旧流程名称",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.log_result",
                        DisplayName = "记录结果",
                    },
                ],
            }
        );
        await service.RunWorkflowAsync(workflow.Id, new MaintenanceWorkflowRunRequest());

        workflow.Name = "新的流程名称";
        await service.SaveWorkflowAsync(workflow);

        var run = Assert.Single(await service.GetRunsAsync());
        Assert.Equal("新的流程名称", run.WorkflowName);
    }

    [Fact]
    public async Task SaveWorkflowAsync_ShouldRemoveRepeatControlsFromCompositeTasks()
    {
        var service = new MaintenanceWorkflowService();

        var saved = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "安全参数",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "task.daily",
                        DisplayName = "每日任务",
                        Parameters = new Dictionary<string, string>
                        {
                            ["executionProbability"] = "80",
                            ["countMin"] = "5",
                            ["countMax"] = "10",
                            ["waitMinSeconds"] = "30",
                            ["waitMaxSeconds"] = "60",
                            ["dailyLimit"] = "20",
                        },
                    },
                ],
            }
        );

        var step = Assert.Single(saved.Steps);
        Assert.Equal("80", step.Parameters["executionProbability"]);
        Assert.DoesNotContain("countMin", step.Parameters.Keys);
        Assert.DoesNotContain("countMax", step.Parameters.Keys);
        Assert.DoesNotContain("waitMinSeconds", step.Parameters.Keys);
        Assert.DoesNotContain("waitMaxSeconds", step.Parameters.Keys);
        Assert.DoesNotContain("dailyLimit", step.Parameters.Keys);
    }

    [Fact]
    public async Task RunWorkflowAsync_ShouldSkipDisabledAndPermissionBlockedStepsWithReasons()
    {
        var service = new MaintenanceWorkflowService();
        var workflow = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "权限检查",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "task.vip_big_point",
                        DisplayName = "大积分签到",
                        Enabled = true,
                        Permission = "big_vip",
                    },
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.log_result",
                        DisplayName = "记录结果",
                        Enabled = false,
                    },
                ],
            }
        );

        var run = await service.RunWorkflowAsync(
            workflow.Id,
            new MaintenanceWorkflowRunRequest
            {
                PermissionSnapshot = new Dictionary<string, bool> { ["big_vip"] = false },
            }
        );

        Assert.Equal(MaintenanceWorkflowRunStatus.Completed, run.Status);
        Assert.Equal(0, run.SuccessCount);
        Assert.Equal(2, run.SkippedCount);
        Assert.Contains(
            run.StepLogs,
            x => x.Status == MaintenanceWorkflowStepStatus.Skipped && x.Message.Contains("缺少权限")
        );
        Assert.Contains(
            run.StepLogs,
            x => x.Status == MaintenanceWorkflowStepStatus.Skipped && x.Message.Contains("已禁用")
        );
    }

    [Fact]
    public async Task RunWorkflowAsync_ShouldNotRunADisabledWorkflow()
    {
        var service = new MaintenanceWorkflowService();
        var workflow = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "已停用流程",
                IsEnabled = false,
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.log_result",
                        DisplayName = "记录结果",
                    },
                ],
            }
        );

        var run = await service.RunWorkflowAsync(workflow.Id, new MaintenanceWorkflowRunRequest());

        Assert.Equal(MaintenanceWorkflowRunStatus.Skipped, run.Status);
        Assert.Empty(run.StepLogs);
        Assert.Contains("已停用", run.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunWorkflowAsync_ShouldHonorTheConfiguredTimeWindow()
    {
        var service = new MaintenanceWorkflowService();
        var now = DateTimeOffset.Now;
        var start = now.AddHours(2).ToString("HH:mm");
        var end = now.AddHours(3).ToString("HH:mm");
        var workflow = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "时段限制",
                IsEnabled = true,
                TimeWindow = $"{start}-{end}",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.log_result",
                        DisplayName = "记录结果",
                    },
                ],
            }
        );

        var run = await service.RunWorkflowAsync(workflow.Id, new MaintenanceWorkflowRunRequest());

        Assert.Equal(MaintenanceWorkflowRunStatus.Skipped, run.Status);
        Assert.Empty(run.StepLogs);
        Assert.Contains("运行时段", run.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveWorkflowAsync_ShouldPreserveCanvasPositionsAndExecuteBySequentialEdges()
    {
        var service = new MaintenanceWorkflowService();

        var saved = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "画布流程",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        Id = "step-log",
                        NodeType = "control.log_result",
                        DisplayName = "记录结果",
                        SortOrder = 1,
                        X = 420,
                        Y = 120,
                    },
                    new MaintenanceWorkflowStep
                    {
                        Id = "step-wait",
                        NodeType = "control.wait",
                        DisplayName = "等待间隔",
                        SortOrder = 2,
                        X = 160,
                        Y = 120,
                    },
                ],
                Edges =
                [
                    new MaintenanceWorkflowEdge
                    {
                        Id = "edge-wait-log",
                        SourceStepId = "step-wait",
                        TargetStepId = "step-log",
                    },
                ],
            }
        );

        Assert.Equal(160, saved.Steps.Single(x => x.Id == "step-wait").X);
        Assert.Equal(120, saved.Steps.Single(x => x.Id == "step-wait").Y);
        Assert.Contains(
            saved.Edges,
            x => x.SourceStepId == "step-wait" && x.TargetStepId == "step-log"
        );

        var run = await service.RunWorkflowAsync(saved.Id, new MaintenanceWorkflowRunRequest());

        Assert.Equal(["step-wait", "step-log"], run.StepLogs.Select(x => x.StepId).ToArray());
    }

    [Fact]
    public async Task RunWorkflowAsync_ShouldHonorWaitInterval()
    {
        var service = new MaintenanceWorkflowService();
        var workflow = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "真实等待",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.wait",
                        DisplayName = "等待间隔",
                        IntervalSeconds = 1,
                    },
                ],
            }
        );

        var startedAt = DateTimeOffset.UtcNow;
        var run = await service.RunWorkflowAsync(workflow.Id, new MaintenanceWorkflowRunRequest());
        var elapsed = DateTimeOffset.UtcNow - startedAt;

        Assert.Equal(MaintenanceWorkflowRunStatus.Completed, run.Status);
        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(850),
            $"实际仅等待 {elapsed.TotalMilliseconds:N0}ms"
        );
    }

    [Fact]
    public async Task RunWorkflowAsync_ShouldEnforceStepTimeout()
    {
        var service = new MaintenanceWorkflowService();
        var workflow = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "步骤超时",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.wait",
                        DisplayName = "等待间隔",
                        IntervalSeconds = 5,
                        MaxRuntimeSeconds = 1,
                        FailureStrategy = "stop",
                    },
                ],
            }
        );

        var startedAt = DateTimeOffset.UtcNow;
        var run = await service.RunWorkflowAsync(workflow.Id, new MaintenanceWorkflowRunRequest());
        var elapsed = DateTimeOffset.UtcNow - startedAt;

        Assert.Equal(MaintenanceWorkflowRunStatus.Failed, run.Status);
        Assert.Contains(
            run.StepLogs,
            log => log.Message.Contains("超时", StringComparison.Ordinal)
        );
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"超时停止耗时 {elapsed.TotalSeconds:N1}s");
    }

    [Fact]
    public async Task StopRunAsync_ShouldCancelActiveWait()
    {
        var service = new MaintenanceWorkflowService();
        var workflow = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "可停止流程",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.wait",
                        DisplayName = "等待间隔",
                        IntervalSeconds = 10,
                    },
                ],
            }
        );

        var runTask = service.RunWorkflowAsync(workflow.Id, new MaintenanceWorkflowRunRequest());
        MaintenanceWorkflowRunRecord? activeRun = null;
        for (var attempt = 0; attempt < 20 && activeRun is null; attempt++)
        {
            await Task.Delay(25);
            activeRun = (await service.GetRunsAsync()).FirstOrDefault(run =>
                run.WorkflowId == workflow.Id && run.Status == MaintenanceWorkflowRunStatus.Running
            );
        }

        Assert.NotNull(activeRun);
        Assert.True(await service.StopRunAsync(activeRun!.Id));

        var completedRun = await runTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MaintenanceWorkflowRunStatus.Cancelled, completedRun.Status);
    }

    [Fact]
    public async Task StopRunAsync_ShouldNotRewriteATerminalRun()
    {
        var service = new MaintenanceWorkflowService();
        var workflow = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "停用流程",
                IsEnabled = false,
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.log_result",
                        DisplayName = "记录结果",
                    },
                ],
            }
        );
        var run = await service.RunWorkflowAsync(workflow.Id, new MaintenanceWorkflowRunRequest());

        var stopped = await service.StopRunAsync(run.Id);

        Assert.False(stopped);
        Assert.Equal(
            MaintenanceWorkflowRunStatus.Skipped,
            (await service.GetRunAsync(run.Id))!.Status
        );
    }

    [Fact]
    public async Task RunWorkflowAsync_ShouldRejectAConcurrentRunOfTheSameWorkflow()
    {
        var service = new MaintenanceWorkflowService();
        var workflow = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "防止重复运行",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.wait",
                        DisplayName = "等待间隔",
                        IntervalSeconds = 1,
                    },
                ],
            }
        );

        var firstRunTask = service.RunWorkflowAsync(
            workflow.Id,
            new MaintenanceWorkflowRunRequest()
        );
        await Task.Delay(100);
        var duplicate = await service.RunWorkflowAsync(
            workflow.Id,
            new MaintenanceWorkflowRunRequest()
        );
        var first = await firstRunTask;

        Assert.Equal(MaintenanceWorkflowRunStatus.Completed, first.Status);
        Assert.Equal(MaintenanceWorkflowRunStatus.Skipped, duplicate.Status);
        Assert.Contains("正在运行", duplicate.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveWorkflowAsync_ShouldRejectCycles()
    {
        var service = new MaintenanceWorkflowService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveWorkflowAsync(
                new MaintenanceWorkflowDefinition
                {
                    Name = "循环流程",
                    Steps =
                    [
                        new MaintenanceWorkflowStep
                        {
                            Id = "step-a",
                            NodeType = "control.wait",
                            DisplayName = "等待 A",
                        },
                        new MaintenanceWorkflowStep
                        {
                            Id = "step-b",
                            NodeType = "control.log_result",
                            DisplayName = "记录 B",
                        },
                    ],
                    Edges =
                    [
                        new MaintenanceWorkflowEdge
                        {
                            SourceStepId = "step-a",
                            TargetStepId = "step-b",
                        },
                        new MaintenanceWorkflowEdge
                        {
                            SourceStepId = "step-b",
                            TargetStepId = "step-a",
                        },
                    ],
                }
            )
        );

        Assert.Contains("循环", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveWorkflowAsync_ShouldRejectMultipleOutputsForOrdinaryActions()
    {
        var service = new MaintenanceWorkflowService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveWorkflowAsync(
                new MaintenanceWorkflowDefinition
                {
                    Name = "多出口流程",
                    Steps =
                    [
                        new MaintenanceWorkflowStep
                        {
                            Id = "source",
                            NodeType = "control.wait",
                            DisplayName = "等待",
                        },
                        new MaintenanceWorkflowStep
                        {
                            Id = "target-a",
                            NodeType = "control.log_result",
                            DisplayName = "记录 A",
                        },
                        new MaintenanceWorkflowStep
                        {
                            Id = "target-b",
                            NodeType = "control.log_result",
                            DisplayName = "记录 B",
                        },
                    ],
                    Edges =
                    [
                        new MaintenanceWorkflowEdge
                        {
                            SourceStepId = "source",
                            TargetStepId = "target-a",
                        },
                        new MaintenanceWorkflowEdge
                        {
                            SourceStepId = "source",
                            TargetStepId = "target-b",
                        },
                    ],
                }
            )
        );

        Assert.Contains("一个后续步骤", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveWorkflowAsync_ShouldRejectUnavailableNodes()
    {
        var service = new MaintenanceWorkflowService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveWorkflowAsync(
                new MaintenanceWorkflowDefinition
                {
                    Name = "无效流程",
                    Steps =
                    [
                        new MaintenanceWorkflowStep
                        {
                            NodeType = "account.set_cookie",
                            DisplayName = "旧账号动作",
                        },
                    ],
                }
            )
        );

        Assert.Contains("不可用", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunWorkflowAsync_ShouldUseTopologicalOrderAcrossMergedPaths()
    {
        var service = new MaintenanceWorkflowService();
        var workflow = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "合流流程",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        Id = "root-a",
                        NodeType = "control.wait",
                        DisplayName = "等待 A",
                        SortOrder = 1,
                    },
                    new MaintenanceWorkflowStep
                    {
                        Id = "root-b",
                        NodeType = "control.wait",
                        DisplayName = "等待 B",
                        SortOrder = 2,
                    },
                    new MaintenanceWorkflowStep
                    {
                        Id = "result",
                        NodeType = "control.log_result",
                        DisplayName = "记录结果",
                        SortOrder = 3,
                    },
                ],
                Edges =
                [
                    new MaintenanceWorkflowEdge
                    {
                        SourceStepId = "root-a",
                        TargetStepId = "result",
                    },
                    new MaintenanceWorkflowEdge
                    {
                        SourceStepId = "root-b",
                        TargetStepId = "result",
                    },
                ],
            }
        );

        var run = await service.RunWorkflowAsync(workflow.Id, new MaintenanceWorkflowRunRequest());

        Assert.Equal(["root-a", "root-b", "result"], run.StepLogs.Select(x => x.StepId).ToArray());
    }

    [Fact]
    public async Task RunWorkflowAsync_ShouldFollowSelectedRandomBranchOnly()
    {
        var service = new MaintenanceWorkflowService();
        var workflow = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "随机分支",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        Id = "decision",
                        NodeType = "control.random",
                        DisplayName = "随机判断",
                        SortOrder = 1,
                        Parameters = new Dictionary<string, string>
                        {
                            ["branchProbability"] = "100",
                        },
                    },
                    new MaintenanceWorkflowStep
                    {
                        Id = "matched",
                        NodeType = "control.log_result",
                        DisplayName = "命中",
                        SortOrder = 2,
                    },
                    new MaintenanceWorkflowStep
                    {
                        Id = "otherwise",
                        NodeType = "control.log_result",
                        DisplayName = "未命中",
                        SortOrder = 3,
                    },
                ],
                Edges =
                [
                    new MaintenanceWorkflowEdge
                    {
                        SourceStepId = "decision",
                        TargetStepId = "matched",
                        Label = "matched",
                    },
                    new MaintenanceWorkflowEdge
                    {
                        SourceStepId = "decision",
                        TargetStepId = "otherwise",
                        Label = "otherwise",
                    },
                ],
            }
        );

        var run = await service.RunWorkflowAsync(workflow.Id, new MaintenanceWorkflowRunRequest());

        Assert.Equal(["decision", "matched"], run.StepLogs.Select(x => x.StepId).ToArray());
        Assert.Contains("命中", run.StepLogs[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunWorkflowAsync_ShouldHonorExecutionProbability()
    {
        var service = new MaintenanceWorkflowService();
        var workflow = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "概率执行",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        Id = "optional",
                        NodeType = "control.log_result",
                        DisplayName = "按概率记录",
                        Parameters = new Dictionary<string, string>
                        {
                            ["executionProbability"] = "0",
                        },
                    },
                ],
            }
        );

        var run = await service.RunWorkflowAsync(workflow.Id, new MaintenanceWorkflowRunRequest());

        Assert.Equal(MaintenanceWorkflowRunStatus.Completed, run.Status);
        Assert.Equal(MaintenanceWorkflowStepStatus.Skipped, Assert.Single(run.StepLogs).Status);
        Assert.Contains("本次未执行", run.StepLogs[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetWorkflowsAsync_ShouldBackupAndMigrateLegacyStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bibi-workflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "workflows.json");
        await File.WriteAllTextAsync(
            storePath,
            """
            {
              "workflows": [
                {
                  "id": "legacy",
                  "name": "旧流程",
                  "steps": [
                    { "id": "account", "nodeType": "account.set_cookie", "displayName": "旧账号节点", "sortOrder": 1 },
                    { "id": "wait", "nodeType": "control.wait", "displayName": "等待", "sortOrder": 2, "intervalSeconds": 1 }
                  ],
                  "edges": [
                    { "id": "old-edge", "sourceStepId": "account", "targetStepId": "wait" }
                  ]
                }
              ],
              "runs": []
            }
            """
        );

        try
        {
            var provider = new ServiceCollection().BuildServiceProvider();
            var options = new StaticOptionsMonitor<LocalWorkbenchOptions>(
                new LocalWorkbenchOptions { WorkflowStorePath = storePath }
            );
            var service = new MaintenanceWorkflowService(
                provider,
                options,
                NullLogger<MaintenanceWorkflowService>.Instance
            );

            var workflow = Assert.Single(await service.GetWorkflowsAsync());

            Assert.Equal(
                MaintenanceWorkflowDefinition.CurrentSchemaVersion,
                workflow.SchemaVersion
            );
            Assert.Equal("control.wait", Assert.Single(workflow.Steps).NodeType);
            Assert.Empty(workflow.Edges);
            Assert.Single(Directory.GetFiles(directory, "workflows.json.backup-*"));
            Assert.Contains(
                "schemaVersion",
                await File.ReadAllTextAsync(storePath),
                StringComparison.OrdinalIgnoreCase
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task GetWorkflowsAsync_ShouldRenameTheLegacyDefaultWorkflowTitle()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bibi-workflow-name-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "workflows.json");
        await File.WriteAllTextAsync(
            storePath,
            """
            {
              "workflows": [
                {
                  "schemaVersion": 2,
                  "id": "default-daily-maintenance",
                  "name": "默认账号维护流程",
                  "steps": [],
                  "edges": []
                }
              ],
              "runs": []
            }
            """
        );

        try
        {
            var provider = new ServiceCollection().BuildServiceProvider();
            var options = new StaticOptionsMonitor<LocalWorkbenchOptions>(
                new LocalWorkbenchOptions { WorkflowStorePath = storePath }
            );
            var service = new MaintenanceWorkflowService(
                provider,
                options,
                NullLogger<MaintenanceWorkflowService>.Instance
            );

            var workflow = Assert.Single(await service.GetWorkflowsAsync());

            Assert.Equal("默认养号流程", workflow.Name);
            using var storedState = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllTextAsync(storePath)
            );
            Assert.Equal(
                "默认养号流程",
                storedState.RootElement.GetProperty("workflows")[0].GetProperty("name").GetString()
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task GetRunsAsync_ShouldRestoreActionEvidenceForUserVerification()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"bibi-workflow-evidence-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "workflows.json");
        await File.WriteAllTextAsync(
            storePath,
            """
            {
              "workflows": [],
              "runs": [
                {
                  "id": "run-evidence",
                  "workflowId": "workflow-evidence",
                  "workflowName": "真实动作验证",
                  "status": 2,
                  "stepLogs": [
                    {
                      "stepId": "watch",
                      "nodeType": "daily.random_watch",
                      "displayName": "随机观看视频",
                      "status": 2,
                      "evidence": [
                        {
                          "accountUid": "123456",
                          "action": "随机观看视频",
                          "targetId": "BV1TEST",
                          "targetTitle": "测试视频",
                          "targetUrl": "https://www.bilibili.com/video/BV1TEST",
                          "outcome": "platform_confirmed",
                          "evidenceId": "evidence-1",
                          "attempt": 1,
                          "quantity": 8,
                          "unit": "秒",
                          "platformCode": 0,
                          "platformMessage": "0",
                          "confirmationMethod": "platform_response",
                          "detail": "平台已确认",
                          "occurredAt": "2026-07-11T09:00:00+08:00"
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """
        );

        try
        {
            var provider = new ServiceCollection().BuildServiceProvider();
            var options = new StaticOptionsMonitor<LocalWorkbenchOptions>(
                new LocalWorkbenchOptions { WorkflowStorePath = storePath }
            );
            var service = new MaintenanceWorkflowService(
                provider,
                options,
                NullLogger<MaintenanceWorkflowService>.Instance
            );

            var run = Assert.Single(await service.GetRunsAsync());
            var evidence = Assert.Single(Assert.Single(run.StepLogs).Evidence);

            Assert.Equal("123456", evidence.AccountUid);
            Assert.Equal("BV1TEST", evidence.TargetId);
            Assert.Equal("测试视频", evidence.TargetTitle);
            Assert.Equal("platform_confirmed", evidence.Outcome);
            Assert.Equal("https://www.bilibili.com/video/BV1TEST", evidence.TargetUrl);
            Assert.Equal("evidence-1", evidence.EvidenceId);
            Assert.Equal(1, evidence.Attempt);
            Assert.Equal(8, evidence.Quantity);
            Assert.Equal("秒", evidence.Unit);
            Assert.Equal(0, evidence.PlatformCode);
            Assert.Equal("platform_response", evidence.ConfirmationMethod);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task GetRunsAsync_ShouldRestoreAccountSnapshotsBeforeAndAfterExecution()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"bibi-workflow-snapshot-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "workflows.json");
        await File.WriteAllTextAsync(
            storePath,
            """
            {
              "workflows": [],
              "runs": [
                {
                  "id": "run-snapshot",
                  "workflowId": "workflow-snapshot",
                  "workflowName": "账号变化验证",
                  "status": 3,
                  "accountSnapshots": [
                    {
                      "accountUid": "123456",
                      "accountName": "测试账号",
                      "phase": "before",
                      "capturedAt": "2026-07-11T09:00:00+08:00",
                      "available": true,
                      "level": 5,
                      "experience": 1000,
                      "coinBalance": 20.5,
                      "followingCount": 10,
                      "dailyWatchCompleted": false,
                      "dailyShareCompleted": false,
                      "dailyCoinExperience": 0
                    },
                    {
                      "accountUid": "123456",
                      "accountName": "测试账号",
                      "phase": "after",
                      "capturedAt": "2026-07-11T09:02:00+08:00",
                      "available": true,
                      "level": 5,
                      "experience": 1010,
                      "coinBalance": 19.5,
                      "followingCount": 11,
                      "dailyWatchCompleted": true,
                      "dailyShareCompleted": true,
                      "dailyCoinExperience": 10
                    }
                  ],
                  "stepLogs": []
                }
              ]
            }
            """
        );

        try
        {
            var provider = new ServiceCollection().BuildServiceProvider();
            var options = new StaticOptionsMonitor<LocalWorkbenchOptions>(
                new LocalWorkbenchOptions { WorkflowStorePath = storePath }
            );
            var service = new MaintenanceWorkflowService(
                provider,
                options,
                NullLogger<MaintenanceWorkflowService>.Instance
            );

            var run = Assert.Single(await service.GetRunsAsync());
            var before = Assert.Single(run.AccountSnapshots, item => item.Phase == "before");
            var after = Assert.Single(run.AccountSnapshots, item => item.Phase == "after");

            Assert.Equal("123456", before.AccountUid);
            Assert.Equal(1000, before.Experience);
            Assert.False(before.DailyWatchCompleted);
            Assert.Equal(1010, after.Experience);
            Assert.True(after.DailyWatchCompleted);
            Assert.Equal(11, after.FollowingCount);
            Assert.Equal(10, after.DailyCoinExperience);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DeleteRunAsync_ShouldRemoveACompletedRun()
    {
        var service = new MaintenanceWorkflowService();
        var workflow = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "可清理记录",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.log_result",
                        DisplayName = "记录结果",
                    },
                ],
            }
        );
        var run = await service.RunWorkflowAsync(workflow.Id, new MaintenanceWorkflowRunRequest());

        var deleted = await service.DeleteRunAsync(run.Id);

        Assert.True(deleted);
        Assert.Null(await service.GetRunAsync(run.Id));
    }

    [Fact]
    public async Task ClearRunsAsync_ShouldOnlyRemoveTheSelectedWorkflowHistory()
    {
        var service = new MaintenanceWorkflowService();
        var first = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "流程一",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.log_result",
                        DisplayName = "记录结果",
                    },
                ],
            }
        );
        var second = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "流程二",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.log_result",
                        DisplayName = "记录结果",
                    },
                ],
            }
        );
        await service.RunWorkflowAsync(first.Id, new MaintenanceWorkflowRunRequest());
        await service.RunWorkflowAsync(second.Id, new MaintenanceWorkflowRunRequest());

        var removed = await service.ClearRunsAsync(first.Id);
        var remaining = await service.GetRunsAsync();

        Assert.Equal(1, removed);
        Assert.DoesNotContain(remaining, run => run.WorkflowId == first.Id);
        Assert.Contains(remaining, run => run.WorkflowId == second.Id);
    }

    [Fact]
    public async Task DeleteWorkflowAsync_ShouldRemoveWorkflowAndCompletedHistoryAtomically()
    {
        var service = new MaintenanceWorkflowService();
        var workflow = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "可完整删除的流程",
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.log_result",
                        DisplayName = "记录结果",
                    },
                ],
            }
        );
        await service.RunWorkflowAsync(workflow.Id, new MaintenanceWorkflowRunRequest());

        var deleted = await service.DeleteWorkflowAsync(workflow.Id);

        Assert.True(deleted);
        Assert.Null(await service.GetWorkflowAsync(workflow.Id));
        Assert.DoesNotContain(await service.GetRunsAsync(), run => run.WorkflowId == workflow.Id);
    }

    [Fact]
    public async Task RemoveAccountAsync_ShouldPruneTheUidFromEveryWorkflowWithoutChangingAllAccountsMode()
    {
        var service = new MaintenanceWorkflowService();
        var selected = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "指定账号流程",
                AccountIds = ["100", "200"],
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.log_result",
                        DisplayName = "记录结果",
                    },
                ],
            }
        );
        var allAccounts = await service.SaveWorkflowAsync(
            new MaintenanceWorkflowDefinition
            {
                Name = "全部账号流程",
                AccountIds = [],
                Steps =
                [
                    new MaintenanceWorkflowStep
                    {
                        NodeType = "control.log_result",
                        DisplayName = "记录结果",
                    },
                ],
            }
        );

        var changed = await service.RemoveAccountAsync("100");

        Assert.Equal(1, changed);
        Assert.Equal(["200"], (await service.GetWorkflowAsync(selected.Id))!.AccountIds);
        Assert.Empty((await service.GetWorkflowAsync(allAccounts.Id))!.AccountIds);
        Assert.Equal(0, await service.RemoveAccountAsync("missing-account"));
    }

    [Fact]
    public async Task RunWorkflowAsync_ShouldRecordTheRealTargetForRandomWatch()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"bibi-workflow-target-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try
        {
            var videoService = new RecordingVideoDomainService();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["BiliBiliCookies:0"] =
                            "DedeUserID=123456; SESSDATA=session; bili_jct=csrf",
                    }
                )
                .Build();
            var provider = new ServiceCollection()
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton<CookieStrFactory<BiliCookie>>()
                .AddSingleton<IVideoDomainService>(videoService)
                .BuildServiceProvider();
            var options = new StaticOptionsMonitor<LocalWorkbenchOptions>(
                new LocalWorkbenchOptions
                {
                    WorkflowStorePath = Path.Combine(directory, "workflows.json"),
                }
            );
            var service = new MaintenanceWorkflowService(
                provider,
                options,
                NullLogger<MaintenanceWorkflowService>.Instance
            );
            var workflow = await service.SaveWorkflowAsync(
                new MaintenanceWorkflowDefinition
                {
                    Name = "随机观看验证",
                    Steps =
                    [
                        new MaintenanceWorkflowStep
                        {
                            Id = "watch",
                            NodeType = "daily.random_watch",
                            DisplayName = "随机观看视频",
                        },
                    ],
                }
            );

            var run = await service.RunWorkflowAsync(
                workflow.Id,
                new MaintenanceWorkflowRunRequest { DryRun = false }
            );

            var log = Assert.Single(run.StepLogs);
            var evidence = Assert.Single(log.Evidence);
            Assert.Equal(MaintenanceWorkflowStepStatus.Success, log.Status);
            Assert.Equal("123456", evidence.AccountUid);
            Assert.Equal("BV1EVIDENCE", evidence.TargetId);
            Assert.Equal("随机观看验证视频", evidence.TargetTitle);
            Assert.Equal("https://www.bilibili.com/video/BV1EVIDENCE", evidence.TargetUrl);
            Assert.Equal("platform_confirmed", evidence.Outcome);
            Assert.Equal(0, evidence.PlatformCode);
            Assert.Equal(9, evidence.Quantity);
            Assert.Equal("秒", evidence.Unit);
            Assert.Equal("platform_response", evidence.ConfirmationMethod);
            Assert.Equal("BV1EVIDENCE", videoService.WatchedVideo?.Bvid);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RunWorkflowAsync_ShouldExcludeDisabledUnifiedAccountsEvenWhenCookieRemains()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"bibi-workflow-enabled-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try
        {
            var videoService = new RecordingVideoDomainService();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["BiliBiliCookies:0"] = "DedeUserID=100; SESSDATA=first; bili_jct=csrf-1",
                        ["BiliBiliCookies:1"] = "DedeUserID=200; SESSDATA=second; bili_jct=csrf-2",
                    }
                )
                .Build();
            var bridge = CreateAccountBridge([
                new ContentAutomationAccount
                {
                    Id = "1",
                    Uid = "100",
                    Enabled = true,
                    LoginStatus = "ok",
                },
                new ContentAutomationAccount
                {
                    Id = "2",
                    Uid = "200",
                    Enabled = false,
                    LoginStatus = "ok",
                },
            ]);
            var provider = new ServiceCollection()
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton<CookieStrFactory<BiliCookie>>()
                .AddSingleton<IVideoDomainService>(videoService)
                .AddSingleton(bridge)
                .BuildServiceProvider();
            var service = CreateService(provider, directory);
            var workflow = await service.SaveWorkflowAsync(
                new MaintenanceWorkflowDefinition
                {
                    Name = "停用账号隔离",
                    Steps =
                    [
                        new MaintenanceWorkflowStep
                        {
                            NodeType = "daily.random_watch",
                            DisplayName = "随机观看视频",
                        },
                    ],
                }
            );

            var run = await service.RunWorkflowAsync(
                workflow.Id,
                new MaintenanceWorkflowRunRequest { DryRun = false }
            );

            Assert.Equal(["100"], videoService.AttemptedUserIds);
            Assert.Equal("100", Assert.Single(Assert.Single(run.StepLogs).Evidence).AccountUid);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RunWorkflowAsync_ShouldRejectAStalePlatformSessionBeforeAnyMaintenanceAction()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"bibi-workflow-stale-login-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try
        {
            var videoService = new RecordingVideoDomainService();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["BiliBiliCookies:0"] = "DedeUserID=100; SESSDATA=expired; bili_jct=csrf-1",
                    }
                )
                .Build();
            var bridge = CreateAccountBridge([
                new ContentAutomationAccount
                {
                    Id = "1",
                    Uid = "100",
                    Enabled = true,
                    LoginStatus = "ok",
                    Cookie = "DedeUserID=100; SESSDATA=expired; bili_jct=csrf-1",
                },
            ]);
            var bridgeProxy = (AccountBridgeProxy)(object)bridge;
            var userInfoApi = DispatchProxy.Create<IUserInfoApi, UserInfoApiProxy>();
            ((UserInfoApiProxy)(object)userInfoApi).IsLoggedIn = false;
            var provider = new ServiceCollection()
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton<CookieStrFactory<BiliCookie>>()
                .AddSingleton<IVideoDomainService>(videoService)
                .AddSingleton(bridge)
                .AddSingleton(userInfoApi)
                .BuildServiceProvider();
            var service = CreateService(provider, directory);
            var workflow = await service.SaveWorkflowAsync(
                new MaintenanceWorkflowDefinition
                {
                    Name = "过期会话拦截",
                    Steps =
                    [
                        new MaintenanceWorkflowStep
                        {
                            NodeType = "daily.random_watch",
                            DisplayName = "随机观看视频",
                        },
                    ],
                }
            );

            var run = await service.RunWorkflowAsync(
                workflow.Id,
                new MaintenanceWorkflowRunRequest { DryRun = false }
            );

            Assert.Empty(videoService.AttemptedUserIds);
            Assert.Equal(MaintenanceWorkflowStepStatus.Failed, Assert.Single(run.StepLogs).Status);
            var update = Assert.Single(bridgeProxy.Updates);
            Assert.Equal("1", update.AccountId);
            Assert.Equal("login_required", update.Request.LoginStatus);
            Assert.True(update.Request.ClearCookie);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RunWorkflowAsync_ShouldContinueGranularActionAfterOneAccountFailsAndRecordBothOutcomes()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"bibi-workflow-partial-granular-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try
        {
            var videoService = new RecordingVideoDomainService { FailingUserId = "100" };
            var configuration = TwoAccountConfiguration();
            var provider = new ServiceCollection()
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton<CookieStrFactory<BiliCookie>>()
                .AddSingleton<IVideoDomainService>(videoService)
                .BuildServiceProvider();
            var service = CreateService(provider, directory);
            var workflow = await service.SaveWorkflowAsync(
                new MaintenanceWorkflowDefinition
                {
                    Name = "单账号失败隔离",
                    Steps =
                    [
                        new MaintenanceWorkflowStep
                        {
                            NodeType = "daily.random_watch",
                            DisplayName = "随机观看视频",
                        },
                    ],
                }
            );

            var run = await service.RunWorkflowAsync(
                workflow.Id,
                new MaintenanceWorkflowRunRequest { DryRun = false }
            );

            Assert.Equal(["100", "200"], videoService.AttemptedUserIds);
            var step = Assert.Single(run.StepLogs);
            Assert.Equal(MaintenanceWorkflowStepStatus.Failed, step.Status);
            Assert.Contains(
                step.Evidence,
                item => item.AccountUid == "100" && item.Outcome == "failed"
            );
            Assert.Contains(
                step.Evidence,
                item => item.AccountUid == "200" && item.Outcome == "platform_confirmed"
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RunWorkflowAsync_ShouldContinueCompositeTaskAfterOneAccountFailsAndRecordBothOutcomes()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"bibi-workflow-partial-composite-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = TwoAccountConfiguration();
            var cookieFactory = new CookieStrFactory<BiliCookie>(configuration);
            var taskService = new PartiallyFailingDailyTaskService(cookieFactory, "100");
            var provider = new ServiceCollection()
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton(cookieFactory)
                .AddSingleton<IDailyTaskAppService>(taskService)
                .BuildServiceProvider();
            var service = CreateService(provider, directory);
            var workflow = await service.SaveWorkflowAsync(
                new MaintenanceWorkflowDefinition
                {
                    Name = "组合任务多账号隔离",
                    Steps =
                    [
                        new MaintenanceWorkflowStep
                        {
                            NodeType = "task.daily",
                            DisplayName = "每日任务",
                        },
                    ],
                }
            );

            var run = await service.RunWorkflowAsync(
                workflow.Id,
                new MaintenanceWorkflowRunRequest { DryRun = false }
            );

            Assert.Equal(["100", "200"], taskService.AttemptedUserIds);
            var step = Assert.Single(run.StepLogs);
            Assert.Equal(MaintenanceWorkflowStepStatus.Failed, step.Status);
            Assert.Contains(
                step.Evidence,
                item => item.AccountUid == "100" && item.Outcome == "failed"
            );
            Assert.Contains(
                step.Evidence,
                item => item.AccountUid == "200" && item.Outcome == "completed"
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static IConfiguration TwoAccountConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["BiliBiliCookies:0"] = "DedeUserID=100; SESSDATA=first; bili_jct=csrf-1",
                    ["BiliBiliCookies:1"] = "DedeUserID=200; SESSDATA=second; bili_jct=csrf-2",
                }
            )
            .Build();

    private static MaintenanceWorkflowService CreateService(
        IServiceProvider provider,
        string directory
    ) =>
        new(
            provider,
            new StaticOptionsMonitor<LocalWorkbenchOptions>(
                new LocalWorkbenchOptions
                {
                    WorkflowStorePath = Path.Combine(directory, "workflows.json"),
                }
            ),
            NullLogger<MaintenanceWorkflowService>.Instance
        );

    private static IContentAutomationBridge CreateAccountBridge(
        IReadOnlyList<ContentAutomationAccount> accounts
    )
    {
        var bridge = DispatchProxy.Create<IContentAutomationBridge, AccountBridgeProxy>();
        ((AccountBridgeProxy)(object)bridge).Accounts = accounts;
        return bridge;
    }

    private sealed class RecordingVideoDomainService : IVideoDomainService
    {
        public VideoInfoDto? WatchedVideo { get; private set; }

        public string FailingUserId { get; init; } = "";

        public List<string> AttemptedUserIds { get; } = [];

        public Task<VideoDetail> GetVideoDetail(string aid) =>
            Task.FromResult(
                new VideoDetail
                {
                    Aid = long.Parse(aid),
                    Bvid = "BV1EVIDENCE",
                    Title = "随机观看验证视频",
                }
            );

        public Task<RankingInfo> GetRandomVideoOfRanking() =>
            Task.FromResult(
                new RankingInfo
                {
                    Aid = 987654,
                    Bvid = "BV1EVIDENCE",
                    Cid = 456789,
                    Copyright = 1,
                    Duration = 120,
                    Title = "随机观看验证视频",
                }
            );

        public Task<UpVideoInfo?> GetRandomVideoOfUp(long upId, int total, BiliCookie ck) =>
            Task.FromResult<UpVideoInfo?>(null);

        public Task<int> GetVideoCountOfUp(long upId, BiliCookie ck) => Task.FromResult(0);

        public Task WatchAndShareVideo(DailyTaskInfo dailyTaskStatus, BiliCookie ck) =>
            Task.CompletedTask;

        public Task WatchVideo(VideoInfoDto videoInfo, BiliCookie ck)
        {
            WatchedVideo = videoInfo;
            return Task.CompletedTask;
        }

        public Task<VideoActionReceipt> WatchVideoWithReceipt(VideoInfoDto videoInfo, BiliCookie ck)
        {
            AttemptedUserIds.Add(ck.UserId);
            if (ck.UserId == FailingUserId)
                throw new InvalidOperationException("测试账号执行失败");
            WatchedVideo = videoInfo;
            return Task.FromResult(
                new VideoActionReceipt
                {
                    PlatformCode = 0,
                    PlatformMessage = "0",
                    Quantity = 9,
                    Unit = "秒",
                    Confirmed = true,
                }
            );
        }

        public Task LikeVideo(VideoInfoDto videoInfo, BiliCookie ck) => Task.CompletedTask;

        public Task<VideoActionReceipt> LikeVideoWithReceipt(
            VideoInfoDto videoInfo,
            BiliCookie ck
        ) => Task.FromResult(VideoActionReceipt.PlatformConfirmed());

        public Task ShareVideo(VideoInfoDto videoInfo, BiliCookie ck) => Task.CompletedTask;

        public Task<VideoActionReceipt> ShareVideoWithReceipt(
            VideoInfoDto videoInfo,
            BiliCookie ck
        ) => Task.FromResult(VideoActionReceipt.PlatformConfirmed());
    }

    private sealed class PartiallyFailingDailyTaskService(
        CookieStrFactory<BiliCookie> cookieFactory,
        string failingUserId
    )
        : BaseMultiAccountsAppService(
            NullLogger<PartiallyFailingDailyTaskService>.Instance,
            cookieFactory
        ),
            IDailyTaskAppService
    {
        public List<string> AttemptedUserIds { get; } = [];

        protected override Task DoTaskAccountAsync(
            BiliCookie ck,
            CancellationToken cancellationToken = default
        )
        {
            AttemptedUserIds.Add(ck.UserId);
            if (ck.UserId == failingUserId)
                throw new InvalidOperationException("测试组合任务失败");
            return Task.CompletedTask;
        }
    }

    private class AccountBridgeProxy : DispatchProxy
    {
        public IReadOnlyList<ContentAutomationAccount> Accounts { get; set; } = [];

        public List<(
            string AccountId,
            ContentAutomationAccountUpdateRequest Request
        )> Updates { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IContentAutomationBridge.GetAccountsAsync))
                return Task.FromResult(Accounts);
            if (targetMethod?.Name == nameof(IContentAutomationBridge.UpdateAccountAsync))
            {
                var accountId = (string)args![0]!;
                var request = (ContentAutomationAccountUpdateRequest)args[1]!;
                Updates.Add((accountId, request));
                foreach (var account in Accounts.Where(item => item.Id == accountId))
                {
                    if (!string.IsNullOrWhiteSpace(request.LoginStatus))
                        account.LoginStatus = request.LoginStatus;
                    if (request.ClearCookie)
                        account.Cookie = "";
                }
                return Task.FromResult(new ContentAutomationAccountUpdateResult { Success = true });
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private class UserInfoApiProxy : DispatchProxy
    {
        public bool IsLoggedIn { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IUserInfoApi.LoginByCookie))
            {
                return Task.FromResult(
                    new BiliApiResponse<UserInfo>
                    {
                        Code = IsLoggedIn ? 0 : -101,
                        Message = IsLoggedIn ? "0" : "账号未登录",
                        Data = new UserInfo
                        {
                            IsLogin = IsLoggedIn,
                            Mid = IsLoggedIn ? 100 : 0,
                            Wbi_img = null!,
                        },
                    }
                );
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
