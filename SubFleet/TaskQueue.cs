using System;
using System.Collections.Generic;

namespace SubFleet;

/// <summary>
/// 顺序任务队列：一步做完才做下一步，像 DailyRoutines 的 TaskHelper 那样。
///
/// 为什么不再用「状态机 + 一堆静态标志」：那种写法里每一步都要自己判断
/// 「我现在到底在哪」，一旦某个窗口没按预期出现，就只能在原地空转或者乱跳。
/// 队列反过来——每一步只回答「我这一步完了没有」，没完就下一帧再问。
/// 卡住的时候还能直接看到卡在第几步。
/// </summary>
public sealed class TaskQueue
{
    private sealed class Step
    {
        public string Name = string.Empty;
        public Func<bool> Body = () => true;
        public double TimeoutSeconds = 60;
        public DateTime StartedAt = DateTime.MinValue;
    }

    private readonly List<Step> steps = new();
    private int index;
    private DateTime startedAt = DateTime.MinValue;
    private double totalTimeoutSeconds = 900;

    /// <summary>当前这一步在做什么，给界面显示。</summary>
    public string StepStatus { get; set; } = string.Empty;

    public string Failure { get; private set; } = string.Empty;

    public bool Failed { get; private set; }

    public bool Running => !this.Failed && this.index < this.steps.Count;

    public bool Idle => !this.Running;

    public string CurrentStep => this.Running ? this.steps[this.index].Name : (this.Failed ? "已中断" : "空闲");

    public string Progress => this.steps.Count == 0
        ? string.Empty
        : $"{Math.Min(this.index + 1, this.steps.Count)}/{this.steps.Count}";

    public TaskQueue Reset(double totalTimeoutSeconds = 900)
    {
        this.steps.Clear();
        this.index = 0;
        this.Failed = false;
        this.Failure = string.Empty;
        this.StepStatus = string.Empty;
        this.startedAt = DateTime.MinValue;
        this.totalTimeoutSeconds = totalTimeoutSeconds;
        return this;
    }

    public TaskQueue Add(string name, Func<bool> body, double timeoutSeconds = 60)
    {
        this.steps.Add(new Step { Name = name, Body = body, TimeoutSeconds = timeoutSeconds });
        return this;
    }

    /// <summary>开始跑。之后每帧调用 <see cref="Tick"/>。</summary>
    public void Start()
    {
        this.index = 0;
        this.Failed = false;
        this.Failure = string.Empty;
        this.startedAt = DateTime.UtcNow;
    }

    public void Tick()
    {
        if (!this.Running)
            return;

        var now = DateTime.UtcNow;

        if ((now - this.startedAt).TotalSeconds > this.totalTimeoutSeconds)
        {
            this.Fail($"整轮超过 {this.totalTimeoutSeconds:F0} 秒没跑完（卡在「{this.CurrentStep}」）");
            return;
        }

        var step = this.steps[this.index];
        if (step.StartedAt == DateTime.MinValue)
            step.StartedAt = now;

        if ((now - step.StartedAt).TotalSeconds > step.TimeoutSeconds)
        {
            this.Fail($"「{step.Name}」超过 {step.TimeoutSeconds:F0} 秒没完成");
            return;
        }

        bool done;
        try
        {
            done = step.Body();
        }
        catch (Exception ex)
        {
            this.Fail($"「{step.Name}」抛异常：{ex.Message}");
            return;
        }

        if (!done)
            return;

        this.index++;
        this.StepStatus = string.Empty;
        if (this.index < this.steps.Count)
            this.steps[this.index].StartedAt = DateTime.MinValue;
    }

    public void Fail(string reason)
    {
        this.Failed = true;
        this.Failure = reason;
    }

    public void Clear() => this.Reset();
}
