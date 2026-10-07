// Procedural sound: no samples, everything is synthesised with WebAudio on demand.
// The context starts on the first user gesture (browsers block audio before that).

export function createSfx() {
  let ctx = null, out = null, echo = null, noise = null, enabled = true, lastTink = 0;
  function unlock() {
    if (!ctx) {
      const AC = window.AudioContext || window.webkitAudioContext; if (!AC) return;
      ctx = new AC();
      const comp = ctx.createDynamicsCompressor(); comp.threshold.value = -14; comp.ratio.value = 6; comp.connect(ctx.destination);
      out = ctx.createGain(); out.gain.value = .55; out.connect(comp);
      // outdoor slap-back: a lowpassed feedback delay
      echo = ctx.createDelay(1); echo.delayTime.value = .19;
      const fb = ctx.createGain(); fb.gain.value = .3; const lp = ctx.createBiquadFilter(); lp.type = 'lowpass'; lp.frequency.value = 1400;
      echo.connect(lp); lp.connect(fb); fb.connect(echo); lp.connect(out);
      const len = ctx.sampleRate | 0; noise = ctx.createBuffer(1, len, ctx.sampleRate);
      const d = noise.getChannelData(0); for (let i = 0; i < len; i++) d[i] = Math.random()*2 - 1;
    }
    if (ctx.state === 'suspended') ctx.resume();
  }
  const ready = () => enabled && ctx && ctx.state === 'running';
  function bus(pan, gain, wet = 0) {
    const g = ctx.createGain(); g.gain.value = gain;
    const p = ctx.createStereoPanner ? ctx.createStereoPanner() : null;
    if (p) { p.pan.value = Math.max(-1, Math.min(1, pan)); g.connect(p); p.connect(out); if (wet) { const w = ctx.createGain(); w.gain.value = wet; p.connect(w); w.connect(echo); } }
    else g.connect(out);
    return g;
  }
  // filtered noise burst with an exponential tail
  function burst(dst, t, { type = 'bandpass', f = 1000, f2 = f, q = .8, a = .002, d = .1, g = 1, off = Math.random()*.5 }) {
    const s = ctx.createBufferSource(); s.buffer = noise;
    const flt = ctx.createBiquadFilter(); flt.type = type; flt.Q.value = q; flt.frequency.setValueAtTime(f, t); flt.frequency.exponentialRampToValueAtTime(Math.max(40, f2), t + d);
    const e = ctx.createGain(); e.gain.setValueAtTime(0, t); e.gain.linearRampToValueAtTime(g, t + a); e.gain.exponentialRampToValueAtTime(.0005, t + a + d);
    s.connect(flt); flt.connect(e); e.connect(dst); s.start(t, off, a + d + .05);
  }
  function tone(dst, t, { f, f2 = f, type = 'sine', a = .001, d = .1, g = .5 }) {
    const o = ctx.createOscillator(); o.type = type; o.frequency.setValueAtTime(f, t); o.frequency.exponentialRampToValueAtTime(Math.max(20, f2), t + a + d);
    const e = ctx.createGain(); e.gain.setValueAtTime(0, t); e.gain.linearRampToValueAtTime(g, t + a); e.gain.exponentialRampToValueAtTime(.0005, t + a + d);
    o.connect(e); e.connect(dst); o.start(t); o.stop(t + a + d + .05);
  }
  const J = (x, j = .08) => x*(1 + (Math.random() - .5)*j);

  return {
    unlock,
    set enabled(v) { enabled = v; }, get enabled() { return enabled; },
    get state() { return ctx ? ctx.state : 'none'; },
    shot(pan = 0) {
      if (!ready()) return; const t = ctx.currentTime, b = bus(pan, .9, .55);
      burst(b, t, { type: 'highpass', f: 2600, q: .5, a: .0008, d: .03, g: 1.1 });               // supersonic crack
      burst(b, t, { type: 'lowpass', f: J(2600), f2: 320, q: .7, a: .002, d: .32, g: .95 });      // muzzle blast
      tone(b, t, { f: J(150), f2: 42, a: .002, d: .2, g: .9 });                                    // chest thump
      burst(b, t + .006, { type: 'bandpass', f: J(5200), q: 3, a: .0005, d: .025, g: .25 });       // action cycling
    },
    dry(pan = 0) {
      if (!ready()) return; const t = ctx.currentTime, b = bus(pan, .5);
      burst(b, t, { type: 'bandpass', f: 3200, q: 4, a: .0005, d: .02, g: .8 }); tone(b, t, { f: 1900, f2: 1400, d: .025, g: .15 });
    },
    click(pan = 0) {
      if (!ready()) return; const t = ctx.currentTime, b = bus(pan, .45);
      burst(b, t, { type: 'bandpass', f: 2400, q: 5, a: .0005, d: .018, g: .7 });
    },
    magOut(pan = 0) {
      if (!ready()) return; const t = ctx.currentTime, b = bus(pan, .5);
      burst(b, t, { type: 'bandpass', f: 2800, q: 4, a: .0005, d: .02, g: .7 }); burst(b, t + .02, { type: 'bandpass', f: 900, f2: 600, q: 1.5, a: .005, d: .08, g: .35 });
    },
    magIn(pan = 0) {
      if (!ready()) return; const t = ctx.currentTime, b = bus(pan, .6);
      burst(b, t, { type: 'bandpass', f: 1500, q: 2, a: .001, d: .04, g: .9 }); tone(b, t, { f: 260, f2: 140, d: .06, g: .35 });
      burst(b, t + .055, { type: 'bandpass', f: 3000, q: 4, a: .0005, d: .02, g: .5 });
    },
    bolt(pan = 0) {
      if (!ready()) return; const t = ctx.currentTime, b = bus(pan, .7);
      burst(b, t, { type: 'bandpass', f: 2200, q: 2, a: .0008, d: .035, g: 1 }); tone(b, t, { f: 3100, f2: 2600, d: .05, g: .12 });
      burst(b, t + .03, { type: 'bandpass', f: 1200, q: 2, a: .001, d: .05, g: .5 });
    },
    tink(pan = 0, v = 1) {
      if (!ready()) return; const t = ctx.currentTime; if (t - lastTink < .025) return; lastTink = t;
      const b = bus(pan, .12*v); const f = 4200 + Math.random()*2200;
      tone(b, t, { f, f2: f*.97, d: .07, g: .5 }); tone(b, t, { f: f*1.52, f2: f*1.5, d: .04, g: .25 });
    },
    mag(pan = 0, v = 1) {
      if (!ready()) return; const t = ctx.currentTime, b = bus(pan, .35*v);
      burst(b, t, { type: 'bandpass', f: 900, q: 1.2, a: .002, d: .07, g: 1 }); tone(b, t, { f: 1700, f2: 1500, d: .05, g: .15 });
    },
    ding(pan = 0, v = 1) {
      if (!ready()) return; const t = ctx.currentTime, b = bus(pan, .38*v, .3), f0 = J(560, .1);
      [[1, 1.4, .5], [2.76, .8, .26], [5.4, .45, .12], [8.93, .25, .06]].forEach(([k, d, g]) => tone(b, t, { f: f0*k, f2: f0*k*.995, a: .001, d, g }));
      burst(b, t, { type: 'highpass', f: 3000, a: .0005, d: .015, g: .5 });
    },
    impact(kind, pan = 0, v = 1) {
      if (!ready()) return; const t = ctx.currentTime, b = bus(pan, .35*v);
      if (kind === 'wood' || kind === 'canvas') { burst(b, t, { type: 'bandpass', f: 900, q: 1.4, a: .001, d: .06, g: 1 }); tone(b, t, { f: 320, f2: 180, d: .05, g: .3 }); }
      else if (kind === 'metal' || kind === 'steel') { tone(b, t, { f: J(1900, .3), f2: 1700, d: .18, g: .25 }); burst(b, t, { type: 'highpass', f: 2500, a: .0005, d: .02, g: .6 }); }
      else if (kind === 'tin') { tone(b, t, { f: J(2600, .3), f2: 2300, d: .12, g: .3 }); burst(b, t, { type: 'bandpass', f: 4000, q: 2, a: .0005, d: .03, g: .6 }); }
      else if (kind === 'concrete' || kind === 'asphalt' || kind === 'gravel') burst(b, t, { type: 'bandpass', f: 2000, q: 1, a: .0005, d: .05, g: .9 });
      else if (kind === 'flesh') { burst(b, t, { type: 'lowpass', f: 1300, f2: 260, a: .001, d: .07, g: 1.3 }); tone(b, t, { f: 170, f2: 70, d: .07, g: .55 }); }
      else if (kind === 'body') { burst(b, t, { type: 'lowpass', f: 600, f2: 120, a: .004, d: .16, g: 1.4 }); tone(b, t, { f: 95, f2: 45, d: .14, g: .7 }); }
      else if (kind === 'rifle') { burst(b, t, { type: 'bandpass', f: 1500, q: 1.5, a: .001, d: .06, g: 1 }); tone(b, t, { f: 900, f2: 700, d: .07, g: .15 }); }
      else burst(b, t, { type: 'lowpass', f: 700, f2: 200, a: .002, d: .09, g: 1 });
    },
    clack(pan = 0) {
      if (!ready()) return; const t = ctx.currentTime, b = bus(pan, .4, .2);
      burst(b, t, { type: 'lowpass', f: 900, f2: 300, a: .002, d: .12, g: 1 }); tone(b, t, { f: 120, f2: 60, d: .12, g: .5 });
    },
    step(pan = 0, v = 1) {
      if (!ready()) return; const t = ctx.currentTime, b = bus(pan, .06*v);
      burst(b, t, { type: 'lowpass', f: 600, f2: 250, a: .004, d: .07, g: 1 });
    }
  };
}
