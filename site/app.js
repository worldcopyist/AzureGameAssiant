(() => {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const state = {
    frames: null,
    analysis: null,
    scenario: null,
    sourceName: '',
    buckets: [],
    drawMeta: null,
  };

  const SCENARIOS = {
    teamfight: {
      label: '示例：团战掉帧（CPU 瓶颈）',
      build: buildTeamfight,
      note: 'CPU 受限 · 90 秒 · 约 5,400 帧',
    },
    shader: {
      label: '示例：着色器编译（加载尖峰）',
      build: buildShaderHitches,
      note: '120 帧场景 · 72 秒 · 周期性编译卡顿',
    },
    healthy: {
      label: '示例：稳定 60 帧（健康基线）',
      build: buildHealthy,
      note: '60 秒 · 无明显卡顿',
    },
    gap: {
      label: '示例：采集中断（质量保护）',
      build: buildWithGaps,
      note: '75 秒 · 含两段 >1 秒采集缺口',
    },
  };

  function mulberry32(seed) {
    let a = seed >>> 0;
    return function next() {
      a = (a + 0x6D2B79F5) >>> 0;
      let t = Math.imul(a ^ (a >>> 15), 1 | a);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  function pushFrame(frames, tSeconds, dt, gpu, rng) {
    const jitter = 0.985 + rng() * 0.03;
    frames.push({
      t: tSeconds * 1000,
      dt,
      gpu: gpu == null ? null : gpu * jitter,
    });
    return tSeconds + dt / 1000;
  }

  function buildTeamfight() {
    const rng = mulberry32(20261115);
    const frames = [];
    const hitches = [
      { at: 20.4, ms: 140 },
      { at: 44.2, ms: 96 },
      { at: 57.1, ms: 210 },
      { at: 63.8, ms: 165 },
    ].map((h) => ({ ...h, done: false }));
    let t = 0;
    while (t < 90) {
      const inFight = (t > 18 && t < 28) || (t > 52 && t < 68);
      let dt = 16.67 * (1 + (rng() - 0.5) * 0.07);
      if (inFight) dt += 3.2 + rng() * 8.5;
      for (const h of hitches) {
        if (!h.done && t >= h.at) {
          dt = h.ms;
          h.done = true;
          break;
        }
      }
      if (rng() > 0.9992) dt += 28 + rng() * 36;
      t = pushFrame(frames, t, dt, dt * (0.46 + rng() * 0.14), rng);
    }
    return frames;
  }

  function buildShaderHitches() {
    const rng = mulberry32(7311);
    const frames = [];
    const hitches = [6.4, 15.1, 24.6, 31.8, 40.5, 49.2, 58.6, 66.9].map((at) => ({
      at,
      ms: 170 + rng() * 260,
      done: false,
    }));
    let t = 0;
    while (t < 72) {
      let dt = 8.33 * (1 + (rng() - 0.5) * 0.05);
      for (const h of hitches) {
        if (!h.done && t >= h.at) {
          dt = h.ms;
          h.done = true;
          break;
        }
      }
      t = pushFrame(frames, t, dt, dt * (0.66 + rng() * 0.12), rng);
    }
    return frames;
  }

  function buildHealthy() {
    const rng = mulberry32(66);
    const frames = [];
    let t = 0;
    while (t < 60) {
      const dt = 16.67 * (1 + (rng() - 0.5) * 0.045);
      t = pushFrame(frames, t, dt, dt * (0.58 + rng() * 0.08), rng);
    }
    return frames;
  }

  function buildWithGaps() {
    const rng = mulberry32(9090);
    const frames = [];
    const gaps = [
      { at: 29.5, ms: 2500, done: false },
      { at: 54.2, ms: 1800, done: false },
    ];
    let t = 0;
    while (t < 75) {
      let dt = 16.67 * (1 + (rng() - 0.5) * 0.06);
      for (const g of gaps) {
        if (!g.done && t >= g.at) {
          dt = g.ms;
          g.done = true;
          break;
        }
      }
      t = pushFrame(frames, t, dt, dt * (0.55 + rng() * 0.1), rng);
    }
    return frames;
  }

  function splitCsvLine(line) {
    const out = [];
    let value = '';
    let quoted = false;
    for (let i = 0; i < line.length; i += 1) {
      const ch = line[i];
      if (quoted) {
        if (ch === '"') {
          if (line[i + 1] === '"') {
            value += '"';
            i += 1;
          } else {
            quoted = false;
          }
        } else {
          value += ch;
        }
      } else if (ch === '"') {
        quoted = true;
      } else if (ch === ',') {
        out.push(value.trim());
        value = '';
      } else {
        value += ch;
      }
    }
    out.push(value.trim());
    return out;
  }

  function parseCsv(text) {
    const lines = text.replace(/^\uFEFF/, '').split(/\r?\n/);
    let headerIndex = -1;
    let header = null;
    for (let i = 0; i < Math.min(lines.length, 40); i += 1) {
      const line = lines[i].trim();
      if (!line || line.startsWith('#')) continue;
      if (line.includes(',')) {
        const cells = splitCsvLine(line);
        const hasInterval = cells.some((c) =>
          /^(msbetweenpresents|frametime|frame_time|frametime_ms|frame_time_ms|dt_ms)$/i.test(c),
        );
        if (hasInterval) {
          header = cells;
          headerIndex = i;
          break;
        }
        if (headerIndex < 0) {
          header = cells;
          headerIndex = i;
        }
      }
    }
    if (!header) throw new Error('没有找到 CSV 表头，请确认导出格式。');

    const index = new Map(header.map((name, i) => [name.toLowerCase(), i]));
    const pick = (...names) => {
      for (const name of names) {
        if (index.has(name.toLowerCase())) return index.get(name.toLowerCase());
      }
      return -1;
    };

    const intervalIdx = pick('msBetweenPresents', 'frametime', 'frame_time', 'frametime_ms', 'frame_time_ms', 'dt_ms');
    if (intervalIdx < 0) {
      throw new Error('CSV 里没有帧间隔列（msBetweenPresents 或 frametime_ms）。');
    }
    const timeIdx = pick('CPUStartTime', 'CPUTime', 'time_ms', 'timestamp', 'time');
    const gpuIdx = pick('msGPUActive', 'msGPUBusy', 'msUntilRenderComplete', 'msGPU');
    const appIdx = pick('Application', 'ProcessName', 'Process');

    let baseTime = null;
    let cursor = 0;
    const frames = [];
    let appName = '';
    const maxRows = 150000;
    for (let i = headerIndex + 1; i < lines.length && frames.length < maxRows; i += 1) {
      const line = lines[i];
      if (!line || !line.trim() || line.startsWith('#')) continue;
      const cells = splitCsvLine(line);
      const dt = Number.parseFloat(cells[intervalIdx]);
      if (!Number.isFinite(dt) || dt <= 0 || dt > 120000) continue;

      let t;
      if (timeIdx >= 0) {
        const raw = Number.parseFloat(cells[timeIdx]);
        if (!Number.isFinite(raw)) continue;
        if (baseTime == null) baseTime = raw;
        t = raw - baseTime;
        if (t < 0) continue;
      } else {
        t = cursor;
      }
      cursor = t + dt;

      let gpu = null;
      if (gpuIdx >= 0) {
        const value = Number.parseFloat(cells[gpuIdx]);
        if (Number.isFinite(value) && value >= 0) gpu = value;
      }
      if (!appName && appIdx >= 0 && cells[appIdx]) {
        appName = cells[appIdx].trim();
      }
      frames.push({ t, dt, gpu });
    }

    if (frames.length < 30) {
      throw new Error(`有效帧只有 ${frames.length} 帧，至少需要 30 帧才能分析。`);
    }
    return { frames, appName };
  }

  function percentile(sorted, p) {
    if (!sorted.length) return 0;
    const idx = Math.min(sorted.length - 1, Math.max(0, Math.floor(p * (sorted.length - 1))));
    return sorted[idx];
  }

  function analyze(frames) {
    if (!frames || !frames.length) throw new Error('没有可分析的帧数据。');

    const gaps = [];
    const valid = [];
    let totalMs = 0;
    for (const frame of frames) {
      totalMs += frame.dt;
      if (frame.dt >= 1000) {
        gaps.push({ at: frame.t - frame.dt, ms: frame.dt });
      } else if (frame.dt > 0) {
        valid.push(frame);
      }
    }
    if (valid.length < 20) throw new Error('有效帧不足 20 帧，无法生成报告。');

    const intervals = valid.map((f) => f.dt).sort((a, b) => a - b);
    const meanDt = valid.reduce((sum, f) => sum + f.dt, 0) / valid.length;
    const median = percentile(intervals, 0.5);
    const worst = intervals[intervals.length - 1];
    const p99 = percentile(intervals, 0.99);
    const p999 = percentile(intervals, 0.999);

    const threshold = Math.max(33.4, median * 2);
    const events = [];
    let current = null;
    for (const frame of valid) {
      if (frame.dt > threshold) {
        if (current && frame.t - current.lastT <= 600) {
          current.lastT = frame.t;
          current.worst = Math.max(current.worst, frame.dt);
          current.frames += 1;
        } else {
          if (current) events.push(current);
          current = {
            at: frame.t - frame.dt,
            lastT: frame.t,
            worst: frame.dt,
            frames: 1,
          };
        }
      }
    }
    if (current) events.push(current);

    const gpuFrames = valid.filter((f) => f.gpu != null);
    let gpuLoad = null;
    if (gpuFrames.length >= valid.length * 0.5 && gpuFrames.length > 0) {
      const sum = gpuFrames.reduce((acc, f) => acc + Math.min(1.5, f.gpu / f.dt), 0);
      gpuLoad = sum / gpuFrames.length;
    }

    const durationS = totalMs / 1000;
    const validDurationS = valid.reduce((sum, f) => sum + f.dt, 0) / 1000;

    const bucketCount = Math.max(1, Math.ceil(durationS));
    const buckets = Array.from({ length: bucketCount }, () => ({
      frames: 0,
      maxDt: 0,
      stutters: 0,
      gap: false,
    }));
    for (const frame of frames) {
      const index = Math.min(bucketCount - 1, Math.max(0, Math.floor((frame.t + frame.dt / 2) / 1000)));
      const bucket = buckets[index];
      if (frame.dt >= 1000) {
        bucket.gap = true;
        continue;
      }
      bucket.frames += 1;
      bucket.maxDt = Math.max(bucket.maxDt, frame.dt);
    }
    for (const event of events) {
      const index = Math.min(bucketCount - 1, Math.max(0, Math.floor(event.at / 1000)));
      buckets[index].stutters += 1;
      buckets[index].maxDt = Math.max(buckets[index].maxDt, event.worst);
    }

    return {
      frames: frames.length,
      validFrames: valid.length,
      durationS,
      validDurationS,
      avgFps: 1000 / meanDt,
      low1: 1000 / p99,
      low01: 1000 / p999,
      worst,
      median,
      threshold,
      events: events.map((e) => ({
        at: e.at / 1000,
        worst: e.worst,
        frames: e.frames,
      })),
      gaps: gaps.map((g) => ({ at: g.at / 1000, ms: g.ms })),
      gapTotalMs: gaps.reduce((sum, g) => sum + g.ms, 0),
      gpuLoad,
      buckets,
    };
  }

  function diagnose(a) {
    const stuttersPerMin = a.events.length / Math.max(1 / 60, a.durationS / 60);
    const gapRatio = a.durationS > 0 ? a.gapTotalMs / (a.durationS * 1000) : 0;
    const fpsLow = a.avgFps < 55;
    const lowRatio = a.avgFps > 0 ? a.low1 / a.avgFps : 1;
    let cause;
    if (a.gaps.length > 0 && gapRatio >= 0.03) {
      cause = 'capture';
    } else if (a.gpuLoad != null && a.gpuLoad >= 0.88 && fpsLow) {
      cause = 'gpu';
    } else if ((a.gpuLoad == null || a.gpuLoad < 0.72) && fpsLow) {
      cause = 'cpu';
    } else if (stuttersPerMin >= 1.2) {
      cause = 'loading';
    } else {
      cause = 'healthy';
    }

    const evidence = [
      `平均 ${a.avgFps.toFixed(1)} FPS，1% Low ${a.low1.toFixed(1)} FPS（相对平均 ${(lowRatio * 100).toFixed(0)}%）`,
      `卡顿 ${a.events.length} 次（约 ${stuttersPerMin.toFixed(1)} 次/分钟），最长单帧 ${a.worst.toFixed(0)} ms`,
    ];
    if (a.gpuLoad != null) {
      evidence.push(`GPU 忙碌占比 ${(a.gpuLoad * 100).toFixed(0)}%（按 GPU 活跃时间 / 帧间隔估算）`);
    } else {
      evidence.push('CSV 没有 GPU 活跃时间列，本次只用帧间隔与卡顿频率判定');
    }
    if (a.gaps.length) {
      evidence.push(`检测到 ${a.gaps.length} 段采集中断，共 ${(a.gapTotalMs / 1000).toFixed(1)} 秒，已从统计中排除`);
    }

    const advice = {
      cpu: [
        '关闭后台浏览器、录屏与 RGB 控制软件，再进同一场景复测一次。',
        '把「线程 / 物理特效 / 视野距离」逐项降一档，观察 1% Low 是否回升。',
        '电源计划切到高性能，确认显卡驱动里的「低延迟模式」没有强制排队。',
        '若仍无改善，用桌面版查看卡顿瞬间占用 CPU 最高的后台进程。',
      ],
      gpu: [
        '先降一档分辨率或渲染比例，再看平均 FPS 是否明显上升。',
        '关闭体积雾、阴影质量与光线追踪等 GPU 重负载项。',
        '开启 DLSS / FSR 质量档，并把帧率上限设为刷新率减 3。',
        '确认 GPU 温度没有触发降频；桌面版会读取温度与功耗作为证据。',
      ],
      loading: [
        '把游戏装到 SSD，避免场景切换时磁盘排队。',
        '首次进入新区域或新特效时，给着色器预编译留出时间，不要立刻开始团战。',
        '关闭后台下载与压缩任务，减少磁盘与 CPU 竞争。',
        '如果尖峰只在固定位置出现，优先怀疑资源流式加载。',
      ],
      capture: [
        '先确认采集本身是否稳定：重新采集同一段游戏，看缺口是否再现。',
        '检查杀软 / 游戏保护是否短暂阻止了采集进程。',
        '桌面版会给出「帧数据中断」质量徽标，并把缺口排除在低帧统计之外。',
        '修好采集后再判断是 CPU、GPU 还是加载问题，否则结论不可信。',
      ],
      healthy: [
        '当前数据下没有明显瓶颈，不需要为了数字继续降画质。',
        '如果体感仍然卡，换更长的时间窗采集，尖峰可能被采样错过。',
        '同时观察网络延迟与服务器 tick，这类问题不在帧数据里。',
      ],
    };

    const verdicts = {
      cpu: {
        title: '主因：CPU / 引擎瓶颈',
        text: '帧时间被 CPU 提交与引擎逻辑拖住，GPU 还有明显余量。',
      },
      gpu: {
        title: '主因：GPU 瓶颈',
        text: 'GPU 几乎全程满载，帧率上限由画质与分辨率决定。',
      },
      loading: {
        title: '主因：加载 / 编译尖峰',
        text: '平均帧率尚可，但固定节奏的长帧更像资源流式加载或着色器编译。',
      },
      capture: {
        title: '先修数据：采集中断',
        text: '数据里存在超过 1 秒的采集缺口。缺口已排除，但它本身会让体感变差。',
      },
      healthy: {
        title: '表现稳定，无需过度优化',
        text: '帧率、1% Low 与卡顿频率都在健康区间，优先保持当前设置。',
      },
    };

    let score = Math.round(
      (Math.min(1, a.avgFps / 60) * 0.45 +
        Math.min(1, a.low1 / 50) * 0.4 +
        (1 - Math.min(1, stuttersPerMin / 6)) * 0.15) *
        100,
    );
    if (cause === 'capture') score = Math.min(score - 10, 68);
    score = Math.max(0, Math.min(100, score));

    let grade = 'D';
    if (score >= 92) grade = 'S';
    else if (score >= 80) grade = 'A';
    else if (score >= 65) grade = 'B';
    else if (score >= 45) grade = 'C';

    return { cause, evidence, advice: advice[cause], ...verdicts[cause], score, grade, stuttersPerMin, lowRatio };
  }

  function setSource(label, mode) {
    const meta = $('source-meta');
    meta.classList.toggle('ready', mode === 'ready');
    meta.classList.toggle('error', mode === 'error');
    $('source-label').textContent = label;
  }

  function formatSeconds(value) {
    const total = Math.max(0, value);
    const minutes = Math.floor(total / 60);
    const seconds = Math.floor(total % 60);
    return `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
  }

  function renderReport(analysis, diagnosis) {
    $('report').setAttribute('aria-busy', 'false');
    $('empty-state').hidden = true;
    $('report-body').hidden = false;

    const ring = $('grade-ring');
    ring.style.setProperty('--pct', String(diagnosis.score));
    $('grade-letter').textContent = diagnosis.grade;
    $('score-value').textContent = String(diagnosis.score);
    $('verdict-title').textContent = diagnosis.title;
    $('verdict-text').textContent = diagnosis.text;

    const chips = [
      { text: `${diagnosis.grade} 级 · ${diagnosis.score} 分`, cls: diagnosis.score >= 80 ? 'good' : diagnosis.score >= 60 ? 'warn' : 'bad' },
      { text: `1% Low ${analysis.low1.toFixed(1)} fps`, cls: analysis.low1 >= 45 ? 'good' : analysis.low1 >= 30 ? 'warn' : 'bad' },
      { text: `卡顿 ${analysis.events.length} 次`, cls: analysis.events.length <= 2 ? 'good' : analysis.events.length <= 6 ? 'warn' : 'bad' },
    ];
    if (analysis.gpuLoad != null) {
      chips.push({
        text: `GPU ${(analysis.gpuLoad * 100).toFixed(0)}%`,
        cls: analysis.gpuLoad >= 0.88 ? 'warn' : 'good',
      });
    }
    $('evidence-chips').replaceChildren(
      ...chips.map((chip) => {
        const span = document.createElement('span');
        span.className = `chip ${chip.cls}`;
        span.textContent = chip.text;
        return span;
      }),
    );

    $('m-avg').textContent = analysis.avgFps.toFixed(1);
    $('m-low1').textContent = analysis.low1.toFixed(1);
    $('m-low01').textContent = analysis.low01.toFixed(1);
    $('m-stutter').textContent = String(analysis.events.length);
    $('m-worst').textContent = analysis.worst >= 100 ? analysis.worst.toFixed(0) : analysis.worst.toFixed(1);
    $('m-duration').textContent = analysis.durationS.toFixed(1);

    $('evidence-list').replaceChildren(
      ...diagnosis.evidence.map((text) => {
        const li = document.createElement('li');
        li.textContent = text;
        return li;
      }),
    );
    $('advice-list').replaceChildren(
      ...diagnosis.advice.map((text) => {
        const li = document.createElement('li');
        li.textContent = text;
        return li;
      }),
    );

    const top = [...analysis.events].sort((a, b) => b.worst - a.worst).slice(0, 4);
    if (top.length) {
      $('stutter-list').replaceChildren(
        ...top.map((event) => {
          const li = document.createElement('li');
          const time = document.createElement('span');
          time.className = 'st-time';
          time.textContent = formatSeconds(event.at);
          const detail = document.createElement('span');
          detail.className = 'st-detail';
          detail.textContent = `第 ${event.at.toFixed(1)} 秒 · 连续 ${event.frames} 帧超阈值`;
          const worst = document.createElement('span');
          worst.className = 'st-worst';
          worst.textContent = `${event.worst.toFixed(0)} ms`;
          li.append(time, detail, worst);
          return li;
        }),
      );
    } else {
      const li = document.createElement('li');
      li.className = 'st-detail';
      li.textContent = '没有超过阈值的帧，时间轴保持平稳。';
      $('stutter-list').replaceChildren(li);
    }

    const quality = $('quality-line');
    if (analysis.gaps.length) {
      quality.textContent = `数据质量：检测到 ${analysis.gaps.length} 段采集中断（共 ${(analysis.gapTotalMs / 1000).toFixed(1)} 秒），已从帧率与低帧统计中排除。`;
      quality.classList.add('bad');
    } else {
      quality.textContent = '数据质量：完整，无超过 1 秒的采集缺口。';
      quality.classList.remove('bad');
    }

    drawTimeline(analysis);
  }

  function drawTimeline(analysis) {
    const wrap = $('canvas-wrap');
    const canvas = $('timeline');
    if (!wrap || !canvas || wrap.clientWidth === 0) return;

    const width = wrap.clientWidth;
    const height = 220;
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    canvas.width = Math.round(width * dpr);
    canvas.height = Math.round(height * dpr);
    canvas.style.height = `${height}px`;

    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, height);

    const margin = { left: 44, right: 12, top: 16, bottom: 24 };
    const plotW = Math.max(10, width - margin.left - margin.right);
    const plotH = Math.max(10, height - margin.top - margin.bottom);
    const duration = Math.max(1, analysis.durationS);
    const buckets = analysis.buckets;

    const peakFps = buckets.reduce((max, b) => Math.max(max, b.frames), 0);
    const steps = [30, 60, 90, 120, 144, 165, 240];
    const niceMax = steps.find((step) => peakFps <= step) || 240;

    const x = (seconds) => margin.left + (seconds / duration) * plotW;
    const y = (fps) => margin.top + plotH - (Math.min(fps, niceMax) / niceMax) * plotH;

    ctx.font = '10px ui-monospace, Consolas, monospace';
    ctx.textBaseline = 'middle';

    const ticks = [];
    for (let step = 0; step <= niceMax; step += 30) ticks.push(step);
    for (const tick of ticks) {
      if (tick > niceMax) continue;
      const ty = y(tick);
      ctx.strokeStyle = 'rgba(255,255,255,0.06)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(margin.left, ty + 0.5);
      ctx.lineTo(margin.left + plotW, ty + 0.5);
      ctx.stroke();
      ctx.fillStyle = '#7f93a6';
      ctx.textAlign = 'right';
      ctx.fillText(String(tick), margin.left - 8, ty);
    }

    const timeTicks = Math.min(6, Math.max(2, Math.floor(duration / 15)));
    for (let i = 0; i <= timeTicks; i += 1) {
      const seconds = (duration / timeTicks) * i;
      const tx = x(seconds);
      ctx.strokeStyle = 'rgba(255,255,255,0.04)';
      ctx.beginPath();
      ctx.moveTo(tx + 0.5, margin.top);
      ctx.lineTo(tx + 0.5, margin.top + plotH);
      ctx.stroke();
      ctx.fillStyle = '#7f93a6';
      ctx.textAlign = i === 0 ? 'left' : i === timeTicks ? 'right' : 'center';
      ctx.fillText(formatSeconds(seconds), tx, height - 10);
    }

    for (const gap of analysis.gaps) {
      const gx = x(gap.at);
      const gw = Math.max(2, (gap.ms / 1000 / duration) * plotW);
      ctx.fillStyle = 'rgba(255,180,84,0.16)';
      ctx.fillRect(gx, margin.top, Math.min(gw, plotW - (gx - margin.left)), plotH);
    }

    const points = buckets.map((b, i) => ({ x: x(i + 0.5), y: y(b.frames), fps: b.frames }));
    if (points.length > 1) {
      const gradient = ctx.createLinearGradient(0, margin.top, 0, margin.top + plotH);
      gradient.addColorStop(0, 'rgba(99,230,190,0.24)');
      gradient.addColorStop(1, 'rgba(99,230,190,0.01)');
      ctx.beginPath();
      ctx.moveTo(points[0].x, margin.top + plotH);
      for (const point of points) ctx.lineTo(point.x, point.y);
      ctx.lineTo(points[points.length - 1].x, margin.top + plotH);
      ctx.closePath();
      ctx.fillStyle = gradient;
      ctx.fill();

      ctx.beginPath();
      for (let i = 0; i < points.length; i += 1) {
        if (i === 0) ctx.moveTo(points[0].x, points[0].y);
        else ctx.lineTo(points[i].x, points[i].y);
      }
      ctx.strokeStyle = '#63e6be';
      ctx.lineWidth = 1.7;
      ctx.lineJoin = 'round';
      ctx.stroke();
    }

    for (const event of analysis.events) {
      const ex = x(event.at);
      const severity = Math.min(1, event.worst / 400);
      ctx.strokeStyle = `rgba(255,107,107,${0.35 + severity * 0.5})`;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(ex + 0.5, margin.top + 2);
      ctx.lineTo(ex + 0.5, margin.top + plotH);
      ctx.stroke();
      ctx.fillStyle = '#ff6b6b';
      ctx.beginPath();
      ctx.moveTo(ex, margin.top + 1);
      ctx.lineTo(ex + 5, margin.top + 1);
      ctx.lineTo(ex + 2.5, margin.top + 7);
      ctx.closePath();
      ctx.fill();
    }

    state.drawMeta = {
      margin,
      plotW,
      plotH,
      duration,
      width,
      height,
      buckets,
      niceMax,
    };
  }

  function showTooltip(event) {
    const meta = state.drawMeta;
    const tooltip = $('tooltip');
    if (!meta) return;
    const wrap = $('canvas-wrap');
    const rect = wrap.getBoundingClientRect();
    const px = event.clientX - rect.left;
    if (px < meta.margin.left || px > meta.margin.left + meta.plotW) {
      tooltip.hidden = true;
      return;
    }
    const ratio = (px - meta.margin.left) / meta.plotW;
    const index = Math.min(meta.buckets.length - 1, Math.max(0, Math.floor(ratio * meta.buckets.length)));
    const bucket = meta.buckets[index];
    const stutters = bucket.stutters;
    tooltip.textContent = [
      `${formatSeconds(index)} · ${bucket.frames} FPS`,
      `最长帧 ${bucket.maxDt ? bucket.maxDt.toFixed(0) : '—'} ms`,
      bucket.gap ? '采集中断' : `卡顿 ${stutters} 次`,
    ].join('\n');
    tooltip.style.whiteSpace = 'pre-line';
    const left = Math.min(px + 14, meta.width - 150);
    tooltip.style.left = `${Math.max(8, left)}px`;
    tooltip.style.top = `${meta.margin.top + 6}px`;
    tooltip.hidden = false;
  }

  function buildReportMarkdown() {
    const a = state.analysis;
    const d = state.currentDiagnosis;
    if (!a || !d) return '';
    const lines = [
      '# 游戏卡顿体检报告',
      '',
      `- 数据来源：${state.sourceName || '内置示例'}`,
      `- 有效帧：${a.validFrames} / ${a.frames}，时长 ${a.durationS.toFixed(1)} 秒`,
      `- 平均 FPS：${a.avgFps.toFixed(1)}`,
      `- 1% Low：${a.low1.toFixed(1)} fps；0.1% Low：${a.low01.toFixed(1)} fps`,
      `- 卡顿事件：${a.events.length} 次；最长单帧：${a.worst.toFixed(0)} ms`,
      `- GPU 忙碌占比：${a.gpuLoad == null ? '数据缺失' : `${(a.gpuLoad * 100).toFixed(0)}%`}`,
      `- 数据质量：${a.gaps.length ? `${a.gaps.length} 段采集中断，共 ${(a.gapTotalMs / 1000).toFixed(1)} 秒（已排除）` : '完整'}`,
      '',
      `## 结论（${d.score} 分 / ${d.grade}）`,
      '',
      `${d.title}。${d.text}`,
      '',
      '## 判定依据',
      '',
      ...d.evidence.map((item) => `- ${item}`),
      '',
      '## 优先尝试',
      '',
      ...d.advice.map((item, i) => `${i + 1}. ${item}`),
      '',
      a.events.length
        ? `## 最明显的卡顿\n\n${[...a.events]
            .sort((m, n) => n.worst - m.worst)
            .slice(0, 5)
            .map((e) => `- 第 ${e.at.toFixed(1)} 秒：${e.worst.toFixed(0)} ms（连续 ${e.frames} 帧）`)
            .join('\n')}`
        : '## 卡顿\n\n没有检测到超过阈值的帧。',
      '',
      '---',
      '由「蓝天游戏助手 · 游戏卡顿体检站」生成，数据仅在浏览器本地解析。',
      '',
    ];
    return lines.join('\n');
  }

  function runAnalysis(frames, sourceName, scenario) {
    state.frames = frames;
    state.scenario = scenario || null;
    state.sourceName = sourceName;
    state.analysis = analyze(frames);
    state.currentDiagnosis = diagnose(state.analysis);
    renderReport(state.analysis, state.currentDiagnosis);
    document.querySelectorAll('.sample').forEach((button) => {
      button.setAttribute('aria-pressed', String(button.dataset.scenario === scenario));
    });
  }

  function loadScenario(id) {
    const scenario = SCENARIOS[id];
    if (!scenario) return;
    setSource(`正在生成 ${scenario.label.replace('示例：', '')}…`, 'loading');
    window.setTimeout(() => {
      try {
        const frames = scenario.build();
        runAnalysis(frames, `内置示例 · ${scenario.note}`, id);
        setSource(`${scenario.label} · ${frames.length} 帧 · 本地生成`, 'ready');
      } catch (error) {
        setSource(error.message, 'error');
      }
    }, 30);
  }

  function handleFile(file) {
    if (!file) return;
    if (file.size > 40 * 1024 * 1024) {
      setSource('文件超过 40 MB，请截取一段再导入。', 'error');
      return;
    }
    setSource(`正在解析 ${file.name}…`, 'loading');
    const reader = new FileReader();
    reader.onerror = () => setSource('读取文件失败，请重试。', 'error');
    reader.onload = () => {
      try {
        const parsed = parseCsv(String(reader.result || ''));
        runAnalysis(parsed.frames, file.name + (parsed.appName ? ` · ${parsed.appName}` : ''), null);
        setSource(`${file.name} · ${parsed.frames.length} 帧 · 本地解析，未上传`, 'ready');
      } catch (error) {
        setSource(error.message || 'CSV 解析失败。', 'error');
      }
    };
    reader.readAsText(file, 'utf-8');
  }

  function init() {
    const dropzone = $('dropzone');
    const input = $('file-input');

    dropzone.addEventListener('click', () => input.click());
    dropzone.addEventListener('keydown', (event) => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        input.click();
      }
    });
    input.addEventListener('change', () => {
      if (input.files && input.files[0]) handleFile(input.files[0]);
      input.value = '';
    });

    for (const type of ['dragenter', 'dragover']) {
      dropzone.addEventListener(type, (event) => {
        event.preventDefault();
        dropzone.classList.add('dragging');
      });
    }
    for (const type of ['dragleave', 'drop']) {
      dropzone.addEventListener(type, (event) => {
        event.preventDefault();
        dropzone.classList.remove('dragging');
      });
    }
    dropzone.addEventListener('drop', (event) => {
      const file = event.dataTransfer?.files?.[0];
      if (file) handleFile(file);
    });

    document.querySelectorAll('.sample').forEach((button) => {
      button.setAttribute('aria-pressed', 'false');
      button.addEventListener('click', () => loadScenario(button.dataset.scenario));
    });

    $('download-report').addEventListener('click', () => {
      const markdown = buildReportMarkdown();
      if (!markdown) return;
      const blob = new Blob([markdown], { type: 'text/markdown;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `卡顿体检-${new Date().toISOString().slice(0, 10)}.md`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 2000);
    });

    $('copy-report').addEventListener('click', async () => {
      const markdown = buildReportMarkdown();
      if (!markdown) return;
      const button = $('copy-report');
      const original = button.textContent;
      try {
        await navigator.clipboard.writeText(markdown);
        button.textContent = '已复制';
      } catch (error) {
        button.textContent = '复制失败，请用下载';
      }
      window.setTimeout(() => {
        button.textContent = original;
      }, 1800);
    });

    $('reset-report').addEventListener('click', () => {
      state.frames = null;
      state.analysis = null;
      state.currentDiagnosis = null;
      state.drawMeta = null;
      $('report-body').hidden = true;
      $('empty-state').hidden = false;
      $('evidence-chips').replaceChildren();
      document.querySelectorAll('.sample').forEach((button) => button.setAttribute('aria-pressed', 'false'));
      setSource('等待数据', 'idle');
    });

    const canvas = $('timeline');
    canvas.addEventListener('pointermove', showTooltip);
    canvas.addEventListener('pointerleave', () => {
      $('tooltip').hidden = true;
    });

    let resizeTimer = 0;
    window.addEventListener('resize', () => {
      window.clearTimeout(resizeTimer);
      resizeTimer = window.setTimeout(() => {
        if (state.analysis) drawTimeline(state.analysis);
      }, 150);
    });

    // Deep link: #lab?demo=teamfight opens a ready report for judges.
    const params = new URLSearchParams(window.location.hash.split('?')[1] || '');
    const demo = params.get('demo');
    if (demo && SCENARIOS[demo]) {
      loadScenario(demo);
    }
  }

  init();
})();
