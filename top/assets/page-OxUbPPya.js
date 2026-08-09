import {r as e} from "./rolldown-runtime-S-ySWqyJ.js";
import {i as t, r as n} from "./framework-CXnKph_e.js";
var r = e(t(), 1)
  , i = [`head`, `neck`, `leadShoulder`, `trailShoulder`, `leadElbow`, `trailElbow`, `leadWrist`, `trailWrist`, `leadHip`, `trailHip`, `leadKnee`, `trailKnee`, `leadAnkle`, `trailAnkle`]
  , a = 4150
  , o = 200
  , s = 300
  , c = 5400
  , l = 5800;
function u(e, t=0, n=1) {
    return Math.min(n, Math.max(t, e))
}
function d(e) {
    let t = u(e);
    return t * t * (3 - 2 * t)
}
function f(e, t, n) {
    let r = Math.max(.001, n - t)
      , i = Math.min(r * .18, Math.max(.001, r * .5));
    if (e <= t)
        return t;
    if (e < t + i) {
        let n = (e - t) / i;
        return t + i * (-n * n * n + 2 * n * n)
    }
    if (e >= n)
        return n;
    if (e > n - i) {
        let t = (n - e) / i;
        return n - i * (-t * t * t + 2 * t * t)
    }
    return e
}
function p(e, t) {
    return Math.atan2(Math.sin(t - e), Math.cos(t - e))
}
function m(e, t) {
    return e + p(e, t)
}
function h(e, t, n, r, i, a) {
    let o = u(a)
      , s = o * o
      , c = s * o
      , l = 2 * c - 3 * s + 1
      , d = c - 2 * s + o
      , f = -2 * c + 3 * s
      , p = c - s;
    return l * e + d * i * n + f * t + p * i * r
}
function g(e, t, n) {
    return {
        x: e.x + (t.x - e.x) * n,
        y: e.y + (t.y - e.y) * n
    }
}
function _(e, t, n) {
    let r = {};
    for (let a of i)
        r[a] = g(e[a], t[a], n);
    return r
}
function v(e) {
    return _(e, e, 0)
}
function y(e, t, n) {
    let r = v(e);
    for (let e of i)
        r[e].x += t,
        r[e].y += n;
    return r
}
function b(...e) {
    let t = e.reduce( (e, t) => ({
        x: e.x + t.x,
        y: e.y + t.y
    }), {
        x: 0,
        y: 0
    });
    return {
        x: t.x / e.length,
        y: t.y / e.length
    }
}
function x(e) {
    return b(e.leadHip, e.trailHip)
}
function S(e, t, n) {
    let r = u(n, e[0].at, e[e.length - 1].at)
      , i = 0;
    for (; i < e.length - 2 && r > e[i + 1].at; )
        i += 1;
    let a = e[Math.max(0, i - 1)]
      , o = e[i]
      , s = e[i + 1]
      , c = e[Math.min(e.length - 1, i + 2)]
      , l = Math.max(1, s.at - o.at)
      , d = u((r - o.at) / l)
      , p = x(e[0].pose)
      , _ = b(e[0].pose.leadShoulder, e[0].pose.trailShoulder)
      , v = Math.atan2(_.y - p.y, _.x - p.x)
      , y = Math.atan2(e[0].pose.leadHip.y - e[0].pose.trailHip.y, e[0].pose.leadHip.x - e[0].pose.trailHip.x)
      , S = Math.cos(y - (v + Math.PI / 2)) >= 0 ? Math.PI / 2 : -Math.PI / 2
      , C = e => {
        let t = x(e)
          , n = b(e.leadShoulder, e.trailShoulder)
          , r = O(e)
          , i = (e, t) => Math.atan2(t.y - e.y, t.x - e.x);
        return {
            rootX: t.x,
            rootY: t.y,
            torso: i(t, n),
            neck: i(n, e.neck),
            head: i(e.neck, e.head),
            leadUpperArm: i(r.leadArm, e.leadElbow),
            leadForearm: i(e.leadElbow, e.leadWrist),
            trailUpperArm: i(r.trailArm, e.trailElbow),
            trailForearm: i(e.trailElbow, e.trailWrist),
            leadThigh: i(r.leadLeg, e.leadKnee),
            leadShin: i(e.leadKnee, e.leadAnkle),
            trailThigh: i(r.trailLeg, e.trailKnee),
            trailShin: i(e.trailKnee, e.trailAnkle)
        }
    }
      , w = C(a.pose)
      , T = C(o.pose)
      , E = C(s.pose)
      , D = C(c.pose)
      , k = new Set([`torso`, `neck`, `head`, `leadUpperArm`, `leadForearm`, `trailUpperArm`, `trailForearm`, `leadThigh`, `leadShin`, `trailThigh`, `trailShin`])
      , A = e => {
        let t = w[e]
          , n = T[e]
          , r = E[e]
          , i = D[e];
        k.has(e) && (t = m(n, t),
        r = m(n, r),
        i = m(r, i));
        let u = (r - t) / Math.max(1, s.at - a.at) * (o.tangentScale ?? .68)
          , f = (i - n) / Math.max(1, c.at - o.at) * (s.tangentScale ?? .68);
        return h(n, r, u, f, l, d)
    }
      , j = {
        x: A(`rootX`),
        y: A(`rootY`)
    }
      , M = (e, t, n) => ({
        x: e.x + Math.cos(t) * n,
        y: e.y + Math.sin(t) * n
    })
      , N = A(`torso`)
      , F = M(j, N, t.torso)
      , I = N + S
      , L = M(j, I, t.hipSpan * .5)
      , R = M(j, I + Math.PI, t.hipSpan * .5)
      , z = M(F, I, t.shoulderSpan * .5)
      , B = M(F, I + Math.PI, t.shoulderSpan * .5)
      , ee = g(z, F, .07)
      , V = g(B, F, .07)
      , te = g(L, j, .02)
      , H = g(R, j, .02)
      , U = P(r)
      , W = (e, t, n, r) => {
        let i = m(e, t)
          , a = Math.PI - r.max * Math.PI / 180
          , o = Math.PI - r.min * Math.PI / 180;
        return e + n * f(Math.abs(i - e), a, o)
    }
      , ne = A(`leadUpperArm`)
      , re = A(`leadForearm`)
      , ie = A(`trailForearm`)
      , ae = M(ee, ne, t.upperArm)
      , oe = M(ae, W(ne, re, 1, U.arms), t.forearm)
      , se = A(`trailUpperArm`)
      , G = M(V, se, t.upperArm)
      , K = M(G, W(se, ie, -1, U.arms), t.forearm)
      , ce = A(`leadThigh`)
      , le = M(te, ce, t.thigh)
      , ue = A(`leadShin`)
      , de = A(`trailShin`)
      , fe = r >= 2670 && r < 5e3 ? 1 : -1
      , pe = M(le, W(ce, ue, 1, U.legs), t.shin)
      , me = A(`trailThigh`)
      , he = M(H, me, t.thigh)
      , ge = M(he, W(me, de, fe, U.legs), t.shin)
      , _e = M(F, A(`neck`), t.neck);
    return {
        head: M(_e, A(`head`), t.head),
        neck: _e,
        leadShoulder: z,
        trailShoulder: B,
        leadElbow: ae,
        trailElbow: G,
        leadWrist: oe,
        trailWrist: K,
        leadHip: L,
        trailHip: R,
        leadKnee: le,
        trailKnee: he,
        leadAnkle: pe,
        trailAnkle: ge
    }
}
function C(e) {
    let t = a - o;
    return e < t ? 0 : e <= a ? d((e - t) / o) : e <= a + s ? 1 - d((e - a) / s) : 0
}
function w(e) {
    return e < 1540 || e >= 9200 ? `guard` : e < 1920 ? `read` : e < 2670 ? `load` : e < 3120 ? `takeoff` : e < a ? `flight` : e < 5e3 ? `contact` : e < c ? `landing` : e < l ? `landingHold` : e < 6450 ? `sideSupport` : e < 6900 ? `kneePlant` : e < 7400 ? `kneeRise` : e < 8200 ? `pushStand` : `recover`
}
function T(e, t) {
    return Math.hypot(t.x - e.x, t.y - e.y)
}
function E(e, t) {
    let n = Math.hypot(e.x, e.y);
    return n > .001 ? {
        x: e.x / n,
        y: e.y / n
    } : t
}
function D(e, t) {
    return e.x * t.x + e.y * t.y
}
function O(e) {
    let t = b(e.leadShoulder, e.trailShoulder)
      , n = b(e.leadHip, e.trailHip);
    return {
        leadArm: g(e.leadShoulder, t, .07),
        trailArm: g(e.trailShoulder, t, .07),
        leadLeg: g(e.leadHip, n, .02),
        trailLeg: g(e.trailHip, n, .02)
    }
}
function k(e, t, n, r) {
    let i = {
        x: t.x - e.x,
        y: t.y - e.y
    }
      , a = Math.hypot(i.x, i.y)
      , o = a > .001 ? {
        x: i.x / a,
        y: i.y / a
    } : r;
    return {
        x: e.x + o.x * n,
        y: e.y + o.y * n
    }
}
function A(e, t, n) {
    let r = Math.cos(n)
      , i = Math.sin(n)
      , a = e.x - t.x
      , o = e.y - t.y;
    return {
        x: t.x + a * r - o * i,
        y: t.y + a * i + o * r
    }
}
function j(e, t, n, r) {
    let i = {
        x: n.x - e.x,
        y: n.y - e.y
    }
      , a = Math.hypot(i.x, i.y);
    if (a < .001)
        return null;
    let o = {
        x: i.x / a,
        y: i.y / a
    };
    if (a >= t + r)
        return {
            x: e.x + o.x * t,
            y: e.y + o.y * t
        };
    if (a <= Math.abs(t - r)) {
        let n = t >= r ? 1 : -1;
        return {
            x: e.x + o.x * t * n,
            y: e.y + o.y * t * n
        }
    }
    let s = (t * t - r * r + a * a) / (2 * a)
      , c = Math.sqrt(Math.max(0, t * t - s * s))
      , l = {
        x: -o.y,
        y: o.x
    }
      , u = {
        x: e.x + o.x * s,
        y: e.y + o.y * s
    }
      , d = {
        x: u.x + l.x * c,
        y: u.y + l.y * c
    }
      , f = {
        x: u.x - l.x * c,
        y: u.y - l.y * c
    }
      , p = i.x * (d.y - e.y) - i.y * (d.x - e.x)
      , m = n.x >= e.x ? 1 : -1;
    return Math.sign(p || 1) === -m ? d : f
}
function M(e, t, n) {
    let r = n * Math.PI / 180;
    return Math.sqrt(Math.max(0, e * e + t * t - 2 * e * t * Math.cos(r)))
}
function N(e, t, n) {
    return {
        min: e.min + (t.min - e.min) * n,
        max: e.max + (t.max - e.max) * n
    }
}
function P(e) {
    let t = [{
        at: 0,
        arms: {
            min: 96,
            max: 116
        },
        legs: {
            min: 148,
            max: 156
        }
    }, {
        at: 1920,
        arms: {
            min: 104,
            max: 124
        },
        legs: {
            min: 145,
            max: 154
        }
    }, {
        at: 2070,
        arms: {
            min: 108,
            max: 132
        },
        legs: {
            min: 125,
            max: 145
        }
    }, {
        at: 2670,
        arms: {
            min: 122,
            max: 146
        },
        legs: {
            min: 130,
            max: 150
        }
    }, {
        at: 3120,
        arms: {
            min: 150,
            max: 166
        },
        legs: {
            min: 145,
            max: 165
        }
    }, {
        at: 3530,
        arms: {
            min: 154,
            max: 178
        },
        legs: {
            min: 145,
            max: 168
        }
    }, {
        at: 4150,
        arms: {
            min: 158,
            max: 178
        },
        legs: {
            min: 145,
            max: 168
        }
    }, {
        at: 4380,
        arms: {
            min: 150,
            max: 176
        },
        legs: {
            min: 140,
            max: 165
        }
    }, {
        at: 5e3,
        arms: {
            min: 126,
            max: 152
        },
        legs: {
            min: 125,
            max: 155
        }
    }, {
        at: 5400,
        arms: {
            min: 104,
            max: 134
        },
        legs: {
            min: 108,
            max: 142
        }
    }, {
        at: 5800,
        arms: {
            min: 100,
            max: 130
        },
        legs: {
            min: 104,
            max: 136
        }
    }, {
        at: 6450,
        arms: {
            min: 96,
            max: 124
        },
        legs: {
            min: 98,
            max: 132
        }
    }, {
        at: 6900,
        arms: {
            min: 100,
            max: 128
        },
        legs: {
            min: 94,
            max: 124
        }
    }, {
        at: 7400,
        arms: {
            min: 104,
            max: 132
        },
        legs: {
            min: 104,
            max: 134
        }
    }, {
        at: 8e3,
        arms: {
            min: 108,
            max: 136
        },
        legs: {
            min: 110,
            max: 145
        }
    }, {
        at: 8600,
        arms: {
            min: 100,
            max: 122
        },
        legs: {
            min: 135,
            max: 152
        }
    }, {
        at: 9200,
        arms: {
            min: 96,
            max: 116
        },
        legs: {
            min: 148,
            max: 156
        }
    }, {
        at: 9600,
        arms: {
            min: 96,
            max: 116
        },
        legs: {
            min: 148,
            max: 156
        }
    }];
    for (let n = 0; n < t.length - 1; n += 1) {
        let r = t[n]
          , i = t[n + 1];
        if (e <= i.at) {
            let t = u((e - r.at) / (i.at - r.at));
            return {
                arms: N(r.arms, i.arms, d(t)),
                legs: N(r.legs, i.legs, d(t))
            }
        }
    }
    return t[t.length - 1]
}
function F(e, t, n, r, i, a, o) {
    let s = {
        x: t.x - e.x,
        y: t.y - e.y
    }
      , c = Math.max(.001, Math.hypot(s.x, s.y))
      , l = M(n, r, a.min)
      , u = M(n, r, a.max)
      , p = {
        x: s.x / c,
        y: s.y / c
    }
      , m = E({
        x: o.x - e.x,
        y: o.y - e.y
    }, p)
      , h = E(g(m, p, d((c - l * .35) / Math.max(.001, l * .5))), m)
      , _ = f(c, l, u)
      , v = {
        x: e.x + h.x * _,
        y: e.y + h.y * _
    }
      , y = (n * n - r * r + _ * _) / (2 * _)
      , b = Math.sqrt(Math.max(0, n * n - y * y))
      , x = {
        x: -h.y,
        y: h.x
    }
      , S = {
        x: e.x + h.x * y + x.x * b * i,
        y: e.y + h.y * y + x.y * b * i
    };
    return {
        joint: S,
        endpoint: v,
        upperAngle: Math.atan2(S.y - e.y, S.x - e.x),
        lowerAngle: Math.atan2(v.y - S.y, v.x - S.x)
    }
}
function I(e, t, n, r, i, a, o) {
    let s = E({
        x: n.x - r.x,
        y: n.y - r.y
    }, {
        x: 0,
        y: 1
    })
      , c = a === 1 ? i : {
        x: -i.x,
        y: -i.y
    }
      , l = {
        x: t.x - e.x,
        y: t.y - e.y
    }
      , u = Math.max(o * .08, D(l, c))
      , d = Math.max(o * .3, D(l, s));
    return {
        outward: c,
        down: s,
        target: {
            x: e.x + c.x * u + s.x * d,
            y: e.y + c.y * u + s.y * d
        }
    }
}
function L(e, t, n, r, i, a, o, s, c, l, d, m=1) {
    let h = u(m);
    if (h <= 0)
        return e;
    let g = Math.max(.001, T(t, e.joint))
      , _ = Math.max(.001, T(e.joint, e.endpoint))
      , v = g + (i - g) * h
      , y = _ + (a - _) * h
      , b = v * s
      , x = v * c
      , S = f(D(E({
        x: n.x - t.x,
        y: n.y - t.y
    }, o.down), o.outward) * v, b, x)
      , C = Math.sqrt(Math.max(0, v * v - S * S))
      , w = {
        x: t.x + o.outward.x * S + o.down.x * C,
        y: t.y + o.outward.y * S + o.down.y * C
    }
      , O = f(D({
        x: r.x - t.x,
        y: r.y - t.y
    }, o.outward) - S, y * l, y * d)
      , k = Math.sqrt(Math.max(0, y * y - O * O))
      , A = {
        x: w.x + o.outward.x * O + o.down.x * k,
        y: w.y + o.outward.y * O + o.down.y * k
    }
      , j = Math.atan2(A.y - w.y, A.x - w.x)
      , M = Math.atan2(w.y - t.y, w.x - t.x)
      , N = e.upperAngle + p(e.upperAngle, M) * h
      , P = e.lowerAngle + p(e.lowerAngle, j) * h
      , F = {
        x: t.x + Math.cos(N) * v,
        y: t.y + Math.sin(N) * v
    };
    return {
        joint: F,
        endpoint: {
            x: F.x + Math.cos(P) * y,
            y: F.y + Math.sin(P) * y
        },
        upperAngle: N,
        lowerAngle: P
    }
}
function R(e, t, n, r, i, a, o, s=1) {
    let c = u(s);
    if (c <= 0)
        return e;
    let l = {
        x: e.endpoint.x,
        y: e.endpoint.y + (Math.min(e.endpoint.y, o) - e.endpoint.y) * c
    }
      , f = D({
        x: l.x - t.x,
        y: l.y - t.y
    }, a.outward)
      , m = r * .08 * c
      , h = Math.sqrt(f * f + m * m);
    l = {
        x: l.x + a.outward.x * (h - f),
        y: l.y + a.outward.y * (h - f)
    };
    let _ = {
        x: l.x - t.x,
        y: l.y - t.y
    }
      , v = Math.max(.001, Math.hypot(_.x, _.y))
      , y = u(v, M(n, r, i.min), M(n, r, i.max))
      , b = {
        x: _.x / v,
        y: _.y / v
    }
      , x = {
        x: t.x + b.x * y,
        y: t.y + b.y * y
    }
      , S = x
      , C = o - t.y
      , w = y - Math.abs(C);
    if (w > 0) {
        let e = Math.sqrt(Math.max(0, y * y - C * C))
          , n = Math.sign(a.outward.x || _.x || 1);
        S = g(x, {
            x: t.x + e * n,
            y: o
        }, d(w / Math.max(1, r * .16)))
    }
    let T = {
        x: S.x - t.x,
        y: S.y - t.y
    }
      , E = Math.max(.001, Math.hypot(T.x, T.y))
      , O = {
        x: T.x / E,
        y: T.y / E
    }
      , k = (n * n - r * r + E * E) / (2 * E)
      , A = Math.sqrt(Math.max(0, n * n - k * k))
      , j = {
        x: -O.y,
        y: O.x
    }
      , N = {
        x: t.x + O.x * k + j.x * A,
        y: t.y + O.y * k + j.y * A
    }
      , P = {
        x: t.x + O.x * k - j.x * A,
        y: t.y + O.y * k - j.y * A
    }
      , F = N.y <= P.y ? N : P
      , I = Math.atan2(F.y - t.y, F.x - t.x)
      , L = Math.atan2(S.y - F.y, S.x - F.x)
      , R = e.upperAngle + p(e.upperAngle, I) * c
      , z = e.lowerAngle + p(e.lowerAngle, L) * c
      , B = {
        x: t.x + Math.cos(R) * n,
        y: t.y + Math.sin(R) * n
    };
    return {
        joint: B,
        endpoint: {
            x: B.x + Math.cos(z) * r,
            y: B.y + Math.sin(z) * r
        },
        upperAngle: R,
        lowerAngle: z
    }
}
function z(e, t, n, r, i, a, o) {
    let s = v(e)
      , c = b(s.leadHip, s.trailHip)
      , l = b(s.leadShoulder, s.trailShoulder)
      , u = E({
        x: l.x - c.x,
        y: l.y - c.y
    }, {
        x: 0,
        y: -1
    })
      , f = {
        x: -u.y,
        y: u.x
    };
    a < 0 && (f = {
        x: -f.x,
        y: -f.y
    }),
    s.leadHip = {
        x: c.x + f.x * t.hipSpan * .5,
        y: c.y + f.y * t.hipSpan * .5
    },
    s.trailHip = {
        x: c.x - f.x * t.hipSpan * .5,
        y: c.y - f.y * t.hipSpan * .5
    };
    let p = {
        x: c.x + u.x * t.torso,
        y: c.y + u.y * t.torso
    };
    s.leadShoulder = {
        x: p.x + f.x * t.shoulderSpan * .5,
        y: p.y + f.y * t.shoulderSpan * .5
    },
    s.trailShoulder = {
        x: p.x - f.x * t.shoulderSpan * .5,
        y: p.y - f.y * t.shoulderSpan * .5
    };
    let m = {
        x: p.x - l.x,
        y: p.y - l.y
    };
    for (let e of [`neck`, `head`, `leadElbow`, `trailElbow`, `leadWrist`, `trailWrist`])
        s[e].x += m.x,
        s[e].y += m.y;
    s.neck = k(p, s.neck, t.neck, {
        x: 0,
        y: -1
    }),
    s.head = k(s.neck, s.head, t.head, {
        x: 0,
        y: -1
    });
    let h = P(n)
      , _ = O(s)
      , y = g(s.leadWrist, r, i)
      , x = s.trailWrist
      , S = F(_.leadArm, y, t.upperArm, t.forearm, -1, h.arms, s.leadElbow);
    s.leadElbow = S.joint,
    s.leadWrist = S.endpoint;
    let C = F(_.trailArm, x, t.upperArm, t.forearm, 1, h.arms, s.trailElbow);
    s.trailElbow = C.joint,
    s.trailWrist = C.endpoint;
    let T = O(s)
      , D = b(s.leadHip, s.trailHip)
      , A = b(s.leadShoulder, s.trailShoulder)
      , j = w(n)
      , M = j === `contact` || j === `landing` || j === `landingHold` || j === `sideSupport` || j === `kneePlant` || j === `kneeRise` || j === `pushStand` || j === `recover` ? {
        x: a,
        y: 0
    } : f
      , N = I(T.leadLeg, s.leadKnee, D, A, M, 1, t.thigh)
      , z = I(T.trailLeg, s.trailKnee, D, A, M, -1, t.thigh)
      , B = {
        joint: s.leadKnee,
        endpoint: s.leadAnkle,
        upperAngle: Math.atan2(s.leadKnee.y - T.leadLeg.y, s.leadKnee.x - T.leadLeg.x),
        lowerAngle: Math.atan2(s.leadAnkle.y - s.leadKnee.y, s.leadAnkle.x - s.leadKnee.x)
    }
      , ee = {
        joint: s.trailKnee,
        endpoint: s.trailAnkle,
        upperAngle: Math.atan2(s.trailKnee.y - T.trailLeg.y, s.trailKnee.x - T.trailLeg.x),
        lowerAngle: Math.atan2(s.trailAnkle.y - s.trailKnee.y, s.trailAnkle.x - s.trailKnee.x)
    }
      , V = +(j === `guard` || j === `read` || j === `load`);
    j === `takeoff` ? V = 1 - d((n - 2870) / 250) : j === `pushStand` ? V = d((n - 7550) / 650) : j === `recover` && (V = 1);
    let te = .08 + .24 * V
      , H = .46 + -.08000000000000002 * V
      , U = .06 + -.24 * V
      , W = .52 + -.64 * V
      , ne = j === `takeoff` ? 1 - d((n - 2740) / 380) : j === `pushStand` ? V : +(j === `guard` || j === `read` || j === `load` || j === `recover`);
    ne > 0 && (B = L(B, T.leadLeg, s.leadKnee, s.leadAnkle, t.thigh, t.shin, N, te, H, U, W, ne),
    ee = L(ee, T.trailLeg, s.trailKnee, s.trailAnkle, t.thigh, t.shin, z, te, H, U, W, ne));
    let re = j === `flight` ? 0 : j === `contact` ? d((n - 4560) / 440) : 1;
    return B = R(B, T.leadLeg, t.thigh, t.shin, h.legs, N, o - 1, re),
    ee = R(ee, T.trailLeg, t.thigh, t.shin, h.legs, z, o - 1, re),
    s.leadKnee = B.joint,
    s.leadAnkle = B.endpoint,
    s.trailKnee = ee.joint,
    s.trailAnkle = ee.endpoint,
    s
}
function B(e, t, n, r, a, o) {
    let s = Math.min(e * .49, 896)
      , c = s / 3
      , l = t * .25 + c * .88
      , f = r ?? l
      , m = o ?? s
      , h = m / 3
      , S = u(Math.min(t * .088, h * .24), 28, 68)
      , C = a ?? e * .65
      , w = f - S * 1.18 - 4
      , D = u((n.x - C) / Math.max(1, m * .5), -1, 1)
      , O = D >= 0 ? 1 : -1
      , N = Math.abs(D)
      , P = O * Math.min(m * .4, m * (.15 * N + .5 * N * N))
      , F = {
        upperArm: S * .57,
        forearm: S * .52,
        thigh: S * .59,
        shin: S * .57,
        torso: S * .69,
        neck: S * .2,
        head: S * .34,
        shoulderSpan: S * .82,
        hipSpan: S * .51
    }
      , I = {
        head: {
            x: C + O * S * .025,
            y: w - S * 1.08
        },
        neck: {
            x: C + O * S * .01,
            y: w - S * .8
        },
        leadShoulder: {
            x: C + S * .41,
            y: w - S * .64
        },
        trailShoulder: {
            x: C - S * .41,
            y: w - S * .64
        },
        leadElbow: {
            x: C + S * .62,
            y: w - S * .31
        },
        trailElbow: {
            x: C - S * .62,
            y: w - S * .31
        },
        leadWrist: {
            x: C + S * .75,
            y: w - S * .03
        },
        trailWrist: {
            x: C - S * .75,
            y: w - S * .03
        },
        leadHip: {
            x: C + S * .25,
            y: w + S * .05
        },
        trailHip: {
            x: C - S * .25,
            y: w + S * .05
        },
        leadKnee: {
            x: C + S * .4,
            y: w + S * .62
        },
        trailKnee: {
            x: C - S * .4,
            y: w + S * .62
        },
        leadAnkle: {
            x: C + S * .4,
            y: w + S * 1.19
        },
        trailAnkle: {
            x: C - S * .4,
            y: w + S * 1.19
        }
    }
      , L = b(I.leadHip, I.trailHip)
      , R = y(I, 0, S * .026);
    R.leadAnkle = {
        ...I.leadAnkle
    },
    R.trailAnkle = {
        ...I.trailAnkle
    },
    R.leadKnee.x += S * .018,
    R.trailKnee.x -= S * .018,
    R.head.x += O * S * .09,
    R.head.y -= S * .035,
    R.neck.x += O * S * .065,
    R.neck.y -= S * .02,
    R.leadShoulder.x += O * S * .075,
    R.leadShoulder.y -= S * .022,
    R.trailShoulder.x += O * S * .04,
    R.trailShoulder.y += S * .014,
    R.leadHip.x += O * S * .018,
    R.trailHip.x -= O * S * .014,
    R.leadWrist.x += O * S * .1,
    R.trailWrist.x += O * S * .06;
    let z = y(R, -O * S * .13, S * .085);
    z.head.x += O * S * .03,
    z.leadShoulder.x -= O * S * .04,
    z.trailShoulder.x += O * S * .05,
    z.leadHip.x -= O * S * .025,
    z.trailHip.x += O * S * .035,
    z.leadKnee.x += O * S * .08,
    z.trailKnee.x -= O * S * .16,
    z.leadWrist.y += S * .075,
    z.trailWrist.y += S * .075,
    z.leadAnkle = v(I).leadAnkle,
    z.trailAnkle = v(I).trailAnkle;
    let B = v(z);
    for (let e of [`head`, `neck`, `leadShoulder`, `trailShoulder`, `leadHip`, `trailHip`])
        B[e].x += O * S * .2,
        B[e].y -= S * .11;
    B.leadElbow.x += O * S * .38,
    B.leadElbow.y -= S * .22,
    B.leadWrist.x += O * S * .57,
    B.leadWrist.y -= S * .36,
    B.trailElbow.x += O * S * .31,
    B.trailElbow.y -= S * .16,
    B.trailWrist.x += O * S * .51,
    B.trailWrist.y -= S * .25,
    B.leadKnee.x += O * S * .06,
    B.leadKnee.y -= S * .11,
    B.leadAnkle.x += O * S * .12,
    B.trailKnee.x -= O * S * .22,
    B.trailKnee.y += S * .07,
    B.trailAnkle.x -= O * S * .02;
    let ee = {
        x: L.x + P * .08,
        y: L.y + S * .015
    }
      , V = b(B.leadHip, B.trailHip)
      , te = {
        x: ee.x - V.x,
        y: ee.y - V.y
    };
    for (let e of i)
        B[e].x += te.x,
        B[e].y += te.y;
    let H = v(B);
    for (let e of [`head`, `neck`, `leadShoulder`, `trailShoulder`, `leadHip`, `trailHip`])
        H[e].x += O * S * .34,
        H[e].y -= S * .21;
    H.head.x += O * S * .12,
    H.leadElbow.x += O * S * .24,
    H.leadElbow.y -= S * .18,
    H.leadWrist.x += O * S * .31,
    H.leadWrist.y -= S * .24,
    H.trailElbow.x += O * S * .23,
    H.trailElbow.y -= S * .11,
    H.trailWrist.x += O * S * .29,
    H.trailWrist.y -= S * .17,
    H.leadKnee.x -= O * S * .1,
    H.leadKnee.y -= S * .31,
    H.leadAnkle.x += O * S * .04,
    H.leadAnkle.y -= S * .04,
    H.trailKnee.x += O * S * .14,
    H.trailKnee.y -= S * .13,
    H.trailAnkle.x -= O * S * .025,
    H.trailAnkle.y -= S * .015;
    let U = {
        x: L.x + P * .3,
        y: L.y - S * .2
    }
      , W = b(H.leadHip, H.trailHip)
      , ne = {
        x: U.x - W.x,
        y: U.y - W.y
    };
    for (let e of i)
        H[e].x += ne.x,
        H[e].y += ne.y;
    H.leadKnee = {
        x: U.x - O * S * .3,
        y: U.y + S * .45
    },
    H.leadAnkle = {
        x: U.x - O * S * .68,
        y: U.y + S * .83
    },
    H.trailKnee = {
        x: U.x - O * S * .42,
        y: U.y + S * .5
    },
    H.trailAnkle = {
        x: U.x - O * S * .86,
        y: U.y + S * .84
    };
    let re = {
        x: L.x + P * .58,
        y: L.y - S * .94
    }
      , ie = {
        x: n.x - re.x,
        y: n.y - re.y
    }
      , ae = Math.hypot(ie.x, ie.y)
      , oe = S * 1.78
      , se = Math.max(0, ae - oe)
      , G = {
        x: re.x + ie.x / Math.max(.001, ae) * se,
        y: re.y + ie.y / Math.max(.001, ae) * se
    }
      , K = k(G, {
        x: G.x + O * S * .5,
        y: G.y - S * .48
    }, F.torso, {
        x: 0,
        y: -1
    })
      , ce = E({
        x: K.x - G.x,
        y: K.y - G.y
    }, {
        x: O * .5,
        y: -.86
    })
      , le = {
        x: -ce.y * O,
        y: ce.x * O
    }
      , ue = {
        x: K.x + le.x * F.shoulderSpan * .5,
        y: K.y + le.y * F.shoulderSpan * .5
    }
      , de = {
        x: K.x - le.x * F.shoulderSpan * .5,
        y: K.y - le.y * F.shoulderSpan * .5
    }
      , fe = g(ue, K, .07)
      , pe = M(F.upperArm, F.forearm, 168)
      , me = j(G, T(G, fe), n, pe)
      , he = u(me ? p(Math.atan2(fe.y - G.y, fe.x - G.x), Math.atan2(me.y - G.y, me.x - G.x)) : 0, -6 * Math.PI / 180, 6 * Math.PI / 180)
      , ge = A(ue, G, he)
      , _e = A(de, G, he)
      , ve = A({
        x: K.x + O * S * .12,
        y: K.y - S * .22
    }, G, he)
      , ye = A({
        x: K.x + O * S * .27,
        y: K.y - S * .5
    }, G, he)
      , be = G
      , xe = {
        leadWrist: {
            x: n.x - O * S * .08,
            y: n.y + S * .02
        },
        trailWrist: {
            x: n.x - O * S * .3,
            y: n.y + S * .18
        },
        leadElbow: {
            x: n.x - O * S * .62,
            y: n.y + S * .2
        },
        trailElbow: {
            x: n.x - O * S * .62,
            y: n.y + S * .32
        },
        leadShoulder: ge,
        trailShoulder: _e,
        neck: ve,
        head: ye,
        leadHip: {
            x: be.x + O * S * .12,
            y: be.y + S * .12
        },
        trailHip: {
            x: be.x - O * S * .12,
            y: be.y - S * .12
        },
        leadKnee: {
            x: G.x - O * S * .48,
            y: G.y + S * .2
        },
        trailKnee: {
            x: G.x - O * S * .4,
            y: G.y + S * .52
        },
        leadAnkle: {
            x: G.x - O * S * 1.08,
            y: G.y + S * .08
        },
        trailAnkle: {
            x: G.x - O * S * .88,
            y: G.y + S * .84
        }
    }
      , Se = _(H, xe, .86);
    Se.leadWrist = g(H.leadWrist, xe.leadWrist, .92),
    Se.trailWrist = g(H.trailWrist, xe.trailWrist, .9),
    Se.head.y -= S * .025;
    let q = v(xe);
    q.leadWrist.x += O * S * .035,
    q.leadWrist.y += S * .045,
    q.leadElbow.x += O * S * .08,
    q.leadElbow.y += S * .11,
    q.trailElbow.x += O * S * .1,
    q.trailElbow.y += S * .12,
    q.trailWrist.x += O * S * .045,
    q.trailWrist.y += S * .045,
    q.head.x += O * S * .025,
    q.head.y += S * .08,
    q.leadHip.x += O * S * .06,
    q.leadHip.y += S * .06,
    q.trailHip.x += O * S * .06,
    q.trailHip.y += S * .06;
    let J = {
        x: L.x + P * .92,
        y: f - S * .24
    }
      , Y = {
        x: J.x + O * S * .63,
        y: f - S * .38
    }
      , X = J
      , Ce = {
        leadShoulder: {
            x: Y.x + O * S * .12,
            y: Y.y + S * .18
        },
        trailShoulder: {
            x: Y.x - O * S * .12,
            y: Y.y - S * .18
        },
        neck: {
            x: Y.x + O * S * .06,
            y: Y.y - S * .19
        },
        head: {
            x: Y.x + O * S * .22,
            y: Y.y - S * .34
        },
        leadHip: {
            x: X.x + O * S * .1,
            y: X.y + S * .15
        },
        trailHip: {
            x: X.x - O * S * .1,
            y: X.y - S * .15
        },
        leadElbow: {
            x: Y.x + O * S * .48,
            y: f - S * .28
        },
        leadWrist: {
            x: Y.x + O * S * .8,
            y: f - S * .21
        },
        trailElbow: {
            x: Y.x + O * S * .06,
            y: f - S * .12
        },
        trailWrist: {
            x: Y.x + O * S * .36,
            y: f - S * .08
        },
        leadKnee: {
            x: J.x - O * S * .42,
            y: f - S * .09
        },
        leadAnkle: {
            x: J.x - O * S * 1,
            y: f - S * .2
        },
        trailKnee: {
            x: J.x - O * S * .68,
            y: f - S * .19
        },
        trailAnkle: {
            x: J.x - O * S * 1.18,
            y: f - S * .04
        }
    }
      , we = {
        x: J.x + O * Math.min(S * .04, Math.abs(P) * .015),
        y: J.y + S * .035
    }
      , Te = y(Ce, we.x - J.x, we.y - J.y);
    Te.head.y += S * .018,
    Te.neck.y += S * .012,
    Te.leadElbow.y += S * .015,
    Te.trailElbow.y += S * .012;
    let Z = {
        x: we.x,
        y: f - S * .46
    }
      , Ee = Math.atan2(Y.y - X.y, Y.x - X.x)
      , De = Ee + p(Ee, -Math.PI / 2) * .5
      , Oe = {
        x: Math.cos(De),
        y: Math.sin(De)
    }
      , ke = {
        x: -Oe.y,
        y: Oe.x
    }
      , Ae = {
        x: Z.x + Oe.x * F.torso,
        y: Z.y + Oe.y * F.torso
    }
      , Q = v(Te);
    Q.leadHip = {
        x: Z.x + ke.x * F.hipSpan * .5 * O,
        y: Z.y + ke.y * F.hipSpan * .5 * O
    },
    Q.trailHip = {
        x: Z.x - ke.x * F.hipSpan * .5 * O,
        y: Z.y - ke.y * F.hipSpan * .5 * O
    },
    Q.leadShoulder = {
        x: Ae.x + ke.x * F.shoulderSpan * .5 * O,
        y: Ae.y + ke.y * F.shoulderSpan * .5 * O
    },
    Q.trailShoulder = {
        x: Ae.x - ke.x * F.shoulderSpan * .5 * O,
        y: Ae.y - ke.y * F.shoulderSpan * .5 * O
    },
    Q.neck = {
        x: Ae.x + Oe.x * S * .2,
        y: Ae.y + Oe.y * S * .2
    },
    Q.head = {
        x: Q.neck.x + Oe.x * S * .27,
        y: Q.neck.y + Oe.y * S * .27
    },
    Q.leadElbow = {
        x: Z.x + O * S * .55,
        y: f - S * .22
    },
    Q.leadWrist = {
        x: Z.x + O * S * .72,
        y: f - S * .06
    },
    Q.trailElbow = {
        x: Z.x - O * S * .22,
        y: f - S * .25
    },
    Q.trailWrist = {
        x: Z.x - O * S * .42,
        y: f - S * .08
    },
    Q.leadKnee = {
        x: Z.x - O * S * .42,
        y: f - S * .12
    },
    Q.leadAnkle = {
        x: Z.x - O * S * .95,
        y: f - S * .025
    },
    Q.trailKnee = {
        x: Z.x - O * S * .1,
        y: f - S * .3
    },
    Q.trailAnkle = {
        x: Z.x + O * S * .18,
        y: f - S * .025
    };
    let je = {
        x: we.x,
        y: f - S * .58
    }
      , Me = y(I, je.x - L.x, je.y - L.y);
    Me.head.x += O * S * .08,
    Me.neck.x += O * S * .055,
    Me.leadElbow = {
        x: je.x + O * S * .5,
        y: f - S * .38
    },
    Me.leadWrist = {
        x: je.x + O * S * .66,
        y: f - S * .16
    },
    Me.trailElbow = {
        x: je.x - O * S * .34,
        y: f - S * .3
    },
    Me.trailWrist = {
        x: je.x - O * S * .52,
        y: f - S * .1
    },
    Me.leadKnee = {
        x: je.x + O * S * .42,
        y: f - S * .33
    },
    Me.leadAnkle = {
        x: je.x + O * S * .72,
        y: f - S * .02
    },
    Me.trailKnee = {
        x: je.x - O * S * .42,
        y: f - S * .055
    },
    Me.trailAnkle = {
        x: je.x - O * S * .82,
        y: f - S * .02
    };
    let Ne = {
        x: we.x,
        y: f - S * .82
    }
      , $ = y(I, Ne.x - L.x, Ne.y - L.y);
    $.head.x += O * S * .055,
    $.neck.x += O * S * .035;
    let Pe = E({
        x: $.leadHip.x - $.trailHip.x,
        y: $.leadHip.y - $.trailHip.y
    }, {
        x: 1,
        y: 0
    });
    $.leadKnee = {
        x: $.leadHip.x + Pe.x * S * .18,
        y: f - S * .4
    },
    $.leadAnkle = {
        x: $.leadHip.x + Pe.x * S * .34,
        y: f - S * .02
    },
    $.trailKnee = {
        x: $.trailHip.x - Pe.x * S * .18,
        y: f - S * .36
    },
    $.trailAnkle = {
        x: $.trailHip.x - Pe.x * S * .38,
        y: f - S * .02
    };
    let Fe = e => g(Ne, L, d((e - 7400) / 1800))
      , Ie = Fe(8e3)
      , Le = y(I, Ie.x - L.x, Ie.y - L.y);
    Le.head.x -= O * S * .06,
    Le.neck.x -= O * S * .04,
    Le.leadShoulder.x -= O * S * .025,
    Le.trailShoulder.x -= O * S * .015,
    Le.leadWrist.x -= O * S * .12,
    Le.trailWrist.x -= O * S * .08,
    Le.leadKnee.x += O * S * .09,
    Le.trailKnee.x -= O * S * .09,
    Le.leadAnkle = {
        x: Ie.x + S * .42,
        y: f - S * .02
    },
    Le.trailAnkle = {
        x: Ie.x - S * .42,
        y: f - S * .02
    };
    let Re = Fe(8600)
      , ze = y(I, Re.x - L.x, Re.y - L.y);
    ze.leadAnkle.x -= O * S * .16,
    ze.trailAnkle.x += O * S * .08,
    ze.leadWrist.x -= O * S * .04,
    ze.trailWrist.x -= O * S * .025;
    let Be = v(I)
      , Ve = {
        x: L.x + P * .84,
        y: J.y - S * .26
    }
      , He = g(U, G, .58)
      , Ue = g(G, Ve, .12);
    return {
        poses: {
            guard: I,
            read: R,
            load: z,
            step: B,
            takeoff: H,
            extension: Se,
            dive: xe,
            save: q,
            landing: Ce,
            landingHold: Te,
            sideSupport: Q,
            kneePlant: Me,
            kneeRise: $,
            pushStand: Le,
            recover: ze,
            reset: Be
        },
        bones: F,
        motion: {
            guardRoot: L,
            readRoot: x(R),
            loadRoot: x(z),
            stepRoot: ee,
            takeoffRoot: U,
            extensionRoot: He,
            diveRoot: G,
            saveRoot: Ue,
            preLandingRoot: Ve,
            landingRoot: J,
            landingHoldRoot: we,
            sideSupportRoot: Z,
            kneePlantRoot: je,
            kneeRiseRoot: Ne,
            pushStandRoot: Ie,
            recoverRoot: Re,
            lateralTravel: P,
            direction: O,
            unit: S
        }
    }
}
function ee(e, t, n, r, i, o, s, f) {
    let {poses: p, bones: m, motion: h} = B(e, t, n, o, s, f)
      , v = b(p.guard.leadHip, p.guard.trailHip)
      , T = b(p.guard.leadShoulder, p.guard.trailShoulder)
      , O = E({
        x: T.x - v.x,
        y: T.y - v.y
    }, {
        x: 0,
        y: -1
    })
      , k = D({
        x: -O.y,
        y: O.x
    }, E({
        x: p.guard.leadHip.x - p.guard.trailHip.x,
        y: p.guard.leadHip.y - p.guard.trailHip.y
    }, {
        x: 1,
        y: 0
    })) >= 0 ? 1 : -1
      , A = _(p.save, p.landing, .72)
      , j = S([{
        at: 0,
        pose: p.guard,
        tangentScale: 0
    }, {
        at: 1540,
        pose: p.guard,
        tangentScale: .5
    }, {
        at: 1920,
        pose: p.read
    }, {
        at: 2070,
        pose: p.load,
        tangentScale: .24
    }, {
        at: 2670,
        pose: p.step
    }, {
        at: 3120,
        pose: p.takeoff
    }, {
        at: 3530,
        pose: p.extension
    }, {
        at: a,
        pose: p.dive
    }, {
        at: 4380,
        pose: p.save,
        tangentScale: .58
    }, {
        at: 5e3,
        pose: A,
        tangentScale: .52
    }, {
        at: c,
        pose: p.landing,
        tangentScale: 0
    }, {
        at: l,
        pose: p.landingHold,
        tangentScale: 0
    }, {
        at: 6450,
        pose: p.sideSupport,
        tangentScale: .55
    }, {
        at: 6900,
        pose: p.kneePlant,
        tangentScale: .5
    }, {
        at: 7400,
        pose: p.kneeRise,
        tangentScale: .55
    }, {
        at: 8e3,
        pose: p.pushStand,
        tangentScale: .58
    }, {
        at: 8600,
        pose: p.recover,
        tangentScale: .58
    }, {
        at: 9200,
        pose: p.reset,
        tangentScale: 0
    }, {
        at: i,
        pose: p.guard,
        tangentScale: 0
    }], m, r)
      , M = c - 5e3
      , N = {
        x: 3 * (h.landingRoot.x - h.preLandingRoot.x) / M,
        y: 3 * (h.landingRoot.y - h.preLandingRoot.y) / M
    }
      , P = [{
        at: 0,
        position: h.guardRoot,
        tangentScale: 0
    }, {
        at: 1540,
        position: h.guardRoot,
        tangentScale: .45
    }, {
        at: 1920,
        position: h.readRoot
    }, {
        at: 2070,
        position: h.loadRoot,
        tangentScale: .24
    }, {
        at: 2670,
        position: h.stepRoot
    }, {
        at: 3120,
        position: h.takeoffRoot
    }, {
        at: 3530,
        position: h.extensionRoot
    }, {
        at: a,
        position: h.diveRoot
    }, {
        at: 4380,
        position: h.saveRoot
    }, {
        at: 5e3,
        position: h.preLandingRoot,
        velocity: N
    }, {
        at: c,
        position: h.landingRoot,
        velocity: {
            x: 0,
            y: 0
        },
        tangentScale: 0
    }, {
        at: l,
        position: h.landingHoldRoot,
        velocity: {
            x: 0,
            y: 0
        },
        tangentScale: 0
    }, {
        at: 6450,
        position: h.sideSupportRoot,
        velocity: {
            x: 0,
            y: 0
        },
        tangentScale: 0
    }, {
        at: 6900,
        position: h.kneePlantRoot,
        velocity: {
            x: 0,
            y: 0
        },
        tangentScale: 0
    }, {
        at: 7400,
        position: h.kneeRiseRoot,
        velocity: {
            x: 0,
            y: 0
        },
        tangentScale: 0
    }, {
        at: 8e3,
        position: h.pushStandRoot
    }, {
        at: 8600,
        position: h.recoverRoot
    }, {
        at: 9200,
        position: h.guardRoot,
        tangentScale: 0
    }, {
        at: i,
        position: h.guardRoot,
        tangentScale: 0
    }]
      , F = r >= 7400 && r <= 9200 ? g(h.kneeRiseRoot, h.guardRoot, d((r - 7400) / 1800)) : te(P, r)
      , I = x(j);
    j = y(j, F.x - I.x, F.y - I.y);
    let L = V(r / 160) * (1 - V((r - 1280) / 180))
      , R = V((r - 9e3) / 160) * (1 - V((r - 9440) / 160))
      , ee = Math.max(0, L, R);
    if (ee > .001) {
        let e = Math.sin(r / i * Math.PI * 2 + .7) * 1.35 * ee;
        for (let t of [`head`, `neck`, `leadShoulder`, `trailShoulder`, `leadElbow`, `trailElbow`, `leadWrist`, `trailWrist`])
            j[t].y += e * .72
    }
    let H = C(r);
    j = z(j, m, r, n, H, k, o ?? t * .25 + (f ?? Math.min(e * .49, 896)) / 3 * .88);
    let U = V((r - 2520) / 360)
      , W = 1 - V((r - 3120) / 330)
      , ne = b(j.leadHip, j.trailHip)
      , re = b(j.leadShoulder, j.trailShoulder)
      , ie = h.direction > 0 ? j.trailAnkle : j.leadAnkle;
    return {
        pose: j,
        root: {
            position: ne,
            lateralTravel: ne.x - h.guardRoot.x,
            airborneHeight: Math.max(0, h.guardRoot.y - ne.y),
            tilt: Math.atan2(ne.y - re.y, ne.x - re.x)
        },
        pushAnchor: ie,
        diveDirection: h.direction,
        animationState: w(r),
        ikWeight: H,
        landingProgress: u((r - 5e3) / (l - 5e3)),
        pushProgress: U * W
    }
}
function V(e) {
    let t = u(e);
    return t < .5 ? 4 * t * t * t : 1 - (-2 * t + 2) ** 3 / 2
}
function te(e, t) {
    if (t <= e[0].at)
        return e[0].position;
    if (t >= e[e.length - 1].at)
        return e[e.length - 1].position;
    let n = 0;
    for (; n < e.length - 1 && t > e[n + 1].at; )
        n += 1;
    let r = e[Math.max(0, n - 1)]
      , i = e[n]
      , a = e[n + 1]
      , o = e[Math.min(e.length - 1, n + 2)]
      , s = Math.max(1, a.at - i.at)
      , c = u((t - i.at) / s)
      , l = Math.max(1, a.at - r.at)
      , d = Math.max(1, o.at - i.at)
      , f = {
        x: i.velocity?.x ?? (a.position.x - r.position.x) / l * (i.tangentScale ?? .72),
        y: i.velocity?.y ?? (a.position.y - r.position.y) / l * (i.tangentScale ?? .72)
    }
      , p = {
        x: a.velocity?.x ?? (o.position.x - i.position.x) / d * (a.tangentScale ?? .72),
        y: a.velocity?.y ?? (o.position.y - i.position.y) / d * (a.tangentScale ?? .72)
    };
    return {
        x: h(i.position.x, a.position.x, f.x, p.x, s, c),
        y: h(i.position.y, a.position.y, f.y, p.y, s, c)
    }
}
function H(e, t, n, r, i, a=1) {
    let o = T(t, n);
    if (o < .1)
        return;
    let s = {
        x: -(n.y - t.y) / o,
        y: (n.x - t.x) / o
    }
      , c = e.createLinearGradient(t.x, t.y, n.x, n.y);
    c.addColorStop(0, `rgba(147, 215, 228, 0.68)`),
    c.addColorStop(.42, `rgba(39, 116, 138, 0.94)`),
    c.addColorStop(1, `rgba(4, 31, 45, 0.9)`);
    let l = {
        x: (t.x + n.x) / 2 + s.x * (r - i) * .52,
        y: (t.y + n.y) / 2 + s.y * (r - i) * .52
    };
    e.save(),
    e.globalAlpha = a,
    e.lineCap = `round`,
    e.lineJoin = `round`,
    e.shadowBlur = 4 * a,
    e.shadowColor = `rgba(0, 211, 240, 0.14)`,
    e.beginPath(),
    e.moveTo(t.x, t.y),
    e.quadraticCurveTo(l.x, l.y, n.x, n.y),
    e.lineWidth = r + i,
    e.strokeStyle = `rgba(3, 28, 43, 0.74)`,
    e.stroke(),
    e.beginPath(),
    e.moveTo(t.x, t.y),
    e.quadraticCurveTo(l.x, l.y, n.x, n.y),
    e.lineWidth = Math.max(1, r * 1.26 + i * .52),
    e.strokeStyle = c,
    e.stroke(),
    e.shadowBlur = 0;
    let u = e.createLinearGradient(t.x, t.y, n.x, n.y);
    u.addColorStop(0, `rgba(224, 253, 255, 0.08)`),
    u.addColorStop(.46, `rgba(201, 250, 255, 0.5)`),
    u.addColorStop(1, `rgba(79, 201, 226, 0.12)`),
    e.beginPath(),
    e.moveTo(t.x - s.x * r * .23, t.y - s.y * r * .23),
    e.quadraticCurveTo(l.x - s.x * Math.max(r, i) * .25, l.y - s.y * Math.max(r, i) * .25, n.x - s.x * i * .24, n.y - s.y * i * .24),
    e.lineWidth = .5,
    e.strokeStyle = u,
    e.stroke(),
    e.restore()
}
function U(e, t, n, r, i, a, o) {
    let s = Math.max(1, T(t, n))
      , c = Math.max(1, T(n, r))
      , l = {
        x: (n.x - t.x) / s,
        y: (n.y - t.y) / s
    }
      , u = {
        x: (r.x - n.x) / c,
        y: (r.y - n.y) / c
    }
      , d = Math.min(s, c) * .035
      , f = (e, t, n, r, i) => {
        let a = 1 - i;
        return {
            x: a * a * a * e.x + 3 * a * a * i * t.x + 3 * a * i * i * n.x + i * i * i * r.x,
            y: a * a * a * e.y + 3 * a * a * i * t.y + 3 * a * i * i * n.y + i * i * i * r.y
        }
    }
      , p = (e, t, n, r, i) => {
        let a = 1 - i;
        return {
            x: 3 * a * a * (t.x - e.x) + 6 * a * i * (n.x - t.x) + 3 * i * i * (r.x - n.x),
            y: 3 * a * a * (t.y - e.y) + 6 * a * i * (n.y - t.y) + 3 * i * i * (r.y - n.y)
        }
    }
      , m = {
        x: t.x + l.x * s * .46,
        y: t.y + l.y * s * .46
    }
      , h = {
        x: n.x - l.x * d,
        y: n.y - l.y * d
    }
      , g = {
        x: n.x + u.x * d,
        y: n.y + u.y * d
    }
      , _ = {
        x: r.x - u.x * c * .42,
        y: r.y - u.y * c * .42
    }
      , v = Math.max(2.8, i * .54)
      , y = Math.max(2.4, a * .38)
      , b = Math.max(1.9, a * .3)
      , x = []
      , S = []
      , C = (e, t, n, r, i, a, o) => {
        for (let s = 0; s <= 12; s += 1) {
            let c = s / 12
              , l = f(t, n, r, i, c)
              , u = p(t, n, r, i, c)
              , d = Math.max(.001, Math.hypot(u.x, u.y))
              , m = a + (o - a) * c;
            e.push({
                point: l,
                normal: {
                    x: -u.y / d,
                    y: u.x / d
                },
                radius: m + Math.sin(Math.PI * c) * Math.min(a, o) * .08
            })
        }
    }
    ;
    C(x, t, m, h, n, v, y),
    C(S, n, g, _, r, y, b);
    let w = [x, S]
      , E = t => {
        let n = t[0];
        e.moveTo(n.point.x + n.normal.x * n.radius, n.point.y + n.normal.y * n.radius);
        for (let n of t.slice(1))
            e.lineTo(n.point.x + n.normal.x * n.radius, n.point.y + n.normal.y * n.radius);
        for (let n of [...t].reverse())
            e.lineTo(n.point.x - n.normal.x * n.radius, n.point.y - n.normal.y * n.radius);
        e.closePath()
    }
      , D = e.createLinearGradient(t.x, t.y, r.x, r.y);
    D.addColorStop(0, `rgba(164, 224, 233, 0.92)`),
    D.addColorStop(.28, `rgba(72, 162, 181, 0.96)`),
    D.addColorStop(.62, `rgba(15, 72, 93, 0.98)`),
    D.addColorStop(1, `rgba(4, 36, 53, 0.96)`),
    e.save(),
    e.globalAlpha = o,
    e.lineJoin = `round`,
    e.shadowBlur = 7 * o,
    e.shadowColor = `rgba(0, 218, 241, 0.17)`,
    e.beginPath(),
    w.forEach(E),
    e.fillStyle = `rgba(1, 22, 34, 0.9)`,
    e.fill(),
    e.shadowBlur = 0,
    e.beginPath(),
    w.forEach(E),
    e.lineWidth = .78,
    e.strokeStyle = D,
    e.fillStyle = D,
    e.fill(),
    e.strokeStyle = `rgba(191, 245, 251, 0.58)`,
    e.stroke();
    let O = e.createLinearGradient(t.x, t.y, r.x, r.y);
    O.addColorStop(0, `rgba(232, 255, 255, 0.12)`),
    O.addColorStop(.38, `rgba(221, 254, 255, 0.54)`),
    O.addColorStop(.74, `rgba(112, 226, 243, 0.16)`),
    O.addColorStop(1, `rgba(95, 211, 231, 0)`),
    e.globalAlpha = o * .88,
    e.lineCap = `round`,
    e.lineWidth = Math.max(.72, Math.min(v, y) * .16),
    e.strokeStyle = O;
    for (let t of w)
        e.beginPath(),
        t.forEach( (t, n) => {
            let r = {
                x: t.point.x - t.normal.x * t.radius * .28,
                y: t.point.y - t.normal.y * t.radius * .28
            };
            n === 0 ? e.moveTo(r.x, r.y) : e.lineTo(r.x, r.y)
        }
        ),
        e.stroke();
    e.globalAlpha = o * .44,
    e.lineWidth = .48,
    e.strokeStyle = `rgba(209, 252, 255, 0.88)`;
    for (let t of w)
        e.beginPath(),
        t.forEach( (t, n) => {
            let r = {
                x: t.point.x + t.normal.x * t.radius * .42,
                y: t.point.y + t.normal.y * t.radius * .42
            };
            n === 0 ? e.moveTo(r.x, r.y) : e.lineTo(r.x, r.y)
        }
        ),
        e.stroke();
    e.restore()
}
function W(e, t, n) {
    let r = b(t.leadShoulder, t.trailShoulder)
      , i = b(t.leadHip, t.trailHip)
      , a = E({
        x: i.x - r.x,
        y: i.y - r.y
    }, {
        x: 0,
        y: 1
    })
      , o = {
        x: a.y,
        y: -a.x
    }
      , s = E({
        x: t.leadShoulder.x - t.trailShoulder.x,
        y: t.leadShoulder.y - t.trailShoulder.y
    }, {
        x: 1,
        y: 0
    });
    D(o, s) < 0 && (o = {
        x: -o.x,
        y: -o.y
    });
    let c = Math.max(1, T(r, i))
      , l = Math.max(1, T(t.leadShoulder, t.trailShoulder))
      , u = {
        x: r.x - a.x * c * .09,
        y: r.y - a.y * c * .09
    }
      , d = {
        x: (t.leadShoulder.x + t.leadHip.x) * .5 + o.x * l * .09,
        y: (t.leadShoulder.y + t.leadHip.y) * .5 + o.y * l * .09
    }
      , f = {
        x: i.x + a.x * c * .08,
        y: i.y + a.y * c * .08
    }
      , p = {
        x: (t.trailShoulder.x + t.trailHip.x) * .5 - o.x * l * .09,
        y: (t.trailShoulder.y + t.trailHip.y) * .5 - o.y * l * .09
    };
    e.save(),
    e.beginPath(),
    e.moveTo(t.trailShoulder.x, t.trailShoulder.y),
    e.quadraticCurveTo(u.x, u.y, t.leadShoulder.x, t.leadShoulder.y),
    e.quadraticCurveTo(d.x, d.y, t.leadHip.x, t.leadHip.y),
    e.quadraticCurveTo(f.x, f.y, t.trailHip.x, t.trailHip.y),
    e.quadraticCurveTo(p.x, p.y, t.trailShoulder.x, t.trailShoulder.y),
    e.closePath(),
    e.globalCompositeOperation = `source-over`,
    e.fillStyle = `rgba(0, 242, 254, 0.15)`,
    e.shadowBlur = 10 * n,
    e.shadowColor = `rgba(0, 242, 254, 0.46)`,
    e.fill(),
    e.shadowBlur = 0,
    e.globalAlpha = .9,
    e.lineWidth = Math.max(.65, .82 * n),
    e.strokeStyle = `rgba(198, 250, 255, 0.88)`,
    e.stroke(),
    e.restore()
}
function ne(e, t, n, r) {
    let i = Math.atan2(t.y - n.y, t.x - n.x) + Math.PI / 2
      , a = e.createRadialGradient(t.x - r * .28, t.y - r * .34, r * .14, t.x, t.y, r);
    a.addColorStop(0, `rgba(231, 254, 255, 0.96)`),
    a.addColorStop(.32, `rgba(105, 193, 211, 0.9)`),
    a.addColorStop(.7, `rgba(22, 88, 109, 0.94)`),
    a.addColorStop(1, `rgba(4, 31, 46, 0.9)`),
    e.save(),
    e.translate(t.x, t.y),
    e.rotate(i),
    e.shadowBlur = r * .56,
    e.shadowColor = `rgba(71, 208, 231, 0.18)`,
    e.beginPath(),
    e.moveTo(-r * .52, -r * .83),
    e.quadraticCurveTo(-r * .08, -r * 1.09, r * .42, -r * .87),
    e.quadraticCurveTo(r * .72, -r * .61, r * .7, -r * .25),
    e.quadraticCurveTo(r * .9, -r * .08, r * .63, r * .06),
    e.quadraticCurveTo(r * .68, r * .49, r * .18, r * .78),
    e.quadraticCurveTo(-r * .35, r * .83, -r * .58, r * .38),
    e.quadraticCurveTo(-r * .79, -r * .15, -r * .52, -r * .83),
    e.closePath(),
    e.fillStyle = a,
    e.fill(),
    e.shadowBlur = 0,
    e.lineWidth = .76,
    e.strokeStyle = `rgba(202, 247, 253, 0.68)`,
    e.stroke(),
    e.save(),
    e.globalAlpha = .46,
    e.beginPath(),
    e.moveTo(-r * .24, -r * .49),
    e.quadraticCurveTo(r * .2, -r * .58, r * .62, -r * .27),
    e.quadraticCurveTo(r * .36, -r * .03, -r * .16, r * .02),
    e.closePath(),
    e.fillStyle = `rgba(4, 34, 49, 0.42)`,
    e.fill(),
    e.beginPath(),
    e.moveTo(-r * .28, r * .25),
    e.quadraticCurveTo(r * .16, r * .61, r * .58, r * .19),
    e.lineWidth = .54,
    e.strokeStyle = `rgba(192, 247, 254, 0.44)`,
    e.stroke(),
    e.restore(),
    e.beginPath(),
    e.moveTo(-r * .18, -r * .78),
    e.quadraticCurveTo(r * .36, -r * .51, r * .61, r * .13),
    e.lineWidth = .52,
    e.strokeStyle = `rgba(210, 251, 255, 0.42)`,
    e.stroke(),
    e.restore()
}
function re(e, t, n, r) {
    let i = Math.atan2(t.y - n.y, t.x - n.x)
      , a = e.createRadialGradient(t.x - r * .24, t.y - r * .28, r * .08, t.x, t.y, r);
    a.addColorStop(0, `rgba(242, 255, 255, 0.98)`),
    a.addColorStop(.38, `rgba(102, 215, 234, 0.72)`),
    a.addColorStop(1, `rgba(10, 63, 81, 0.58)`),
    e.save(),
    e.translate(t.x, t.y),
    e.rotate(i),
    e.beginPath(),
    e.moveTo(-r * .62, -r * .45),
    e.quadraticCurveTo(-r * .12, -r * .84, r * .33, -r * .62),
    e.quadraticCurveTo(r * .92, -r * .4, r * .98, r * .04),
    e.quadraticCurveTo(r * .82, r * .53, r * .26, r * .62),
    e.quadraticCurveTo(-r * .32, r * .7, -r * .67, r * .34),
    e.quadraticCurveTo(-r * .92, -r * .02, -r * .62, -r * .45),
    e.closePath(),
    e.fillStyle = a,
    e.fill(),
    e.lineWidth = .54,
    e.strokeStyle = `rgba(224, 254, 255, 0.6)`,
    e.stroke(),
    e.strokeStyle = `rgba(208, 252, 255, 0.42)`,
    e.lineWidth = .44;
    for (let t of [-.27, 0, .25])
        e.beginPath(),
        e.moveTo(r * .08, t * r * .78),
        e.quadraticCurveTo(r * .45, t * r * .96, r * .84, t * r * .72),
        e.stroke();
    e.restore()
}
function ie(e, t, n, r, i, a=0, o=1) {
    let s = Math.atan2(t.y - n.y, t.x - n.x)
      , c = Math.min(1, Math.abs(Math.cos(s)) * 1.4)
      , l = Math.PI * .04 + s * c - a * o * .34
      , u = e.createLinearGradient(-r, 0, r * 1.2, 0);
    u.addColorStop(0, `rgba(5, 34, 49, 0.94)`),
    u.addColorStop(.56, `rgba(39, 111, 132, 0.9)`),
    u.addColorStop(1, `rgba(125, 211, 225, 0.7)`),
    e.save(),
    e.globalAlpha = i,
    e.translate(t.x + o * r * a * .34, t.y - r * a * .18),
    e.rotate(l),
    e.beginPath(),
    e.moveTo(-r * .82, -r * .28),
    e.quadraticCurveTo(-r * .28, -r * .5, r * .48, -r * .33),
    e.quadraticCurveTo(r * 1.14, -r * .22, r * 1.22, r * .13),
    e.quadraticCurveTo(r * .76, r * .48, r * .04, r * .44),
    e.quadraticCurveTo(-r * .63, r * .37, -r * .9, r * .1),
    e.closePath(),
    e.fillStyle = u,
    e.fill(),
    e.lineWidth = .55,
    e.strokeStyle = `rgba(185, 243, 251, 0.48)`,
    e.stroke(),
    e.beginPath(),
    e.moveTo(-r * .54, r * .29),
    e.quadraticCurveTo(r * .22, r * .44, r * 1.06, r * .12),
    e.lineWidth = .45,
    e.strokeStyle = `rgba(214, 253, 255, 0.46)`,
    e.stroke(),
    e.restore()
}
function ae(e, t, n, r, i, a) {
    let o = Math.max(.001, T(t, n))
      , s = Math.max(.001, T(n, r))
      , c = {
        x: (n.x - t.x) / o,
        y: (n.y - t.y) / o
    }
      , l = {
        x: (r.x - n.x) / s,
        y: (r.y - n.y) / s
    }
      , u = {
        x: c.x + l.x,
        y: c.y + l.y
    }
      , d = Math.max(.001, Math.hypot(u.x, u.y))
      , f = {
        x: u.x / d,
        y: u.y / d
    }
      , p = {
        x: -f.y,
        y: f.x
    };
    e.save(),
    e.globalAlpha = a * .92,
    e.beginPath(),
    e.moveTo(n.x + f.x * i * .7, n.y + f.y * i * .7),
    e.lineTo(n.x + p.x * i * .72, n.y + p.y * i * .72),
    e.lineTo(n.x - f.x * i * .76, n.y - f.y * i * .76),
    e.lineTo(n.x - p.x * i * .72, n.y - p.y * i * .72),
    e.closePath();
    let m = e.createRadialGradient(n.x - p.x * i * .28, n.y - p.y * i * .28, i * .08, n.x, n.y, i);
    m.addColorStop(0, `rgba(211, 251, 255, 0.82)`),
    m.addColorStop(.42, `rgba(54, 160, 181, 0.82)`),
    m.addColorStop(1, `rgba(4, 42, 59, 0.94)`),
    e.fillStyle = m,
    e.fill(),
    e.lineWidth = .58,
    e.strokeStyle = `rgba(202, 248, 255, 0.54)`,
    e.stroke(),
    e.globalAlpha = a * .56,
    e.beginPath(),
    e.moveTo(n.x - p.x * i * .54, n.y - p.y * i * .54),
    e.lineTo(n.x + p.x * i * .54, n.y + p.y * i * .54),
    e.lineWidth = .48,
    e.strokeStyle = `rgba(221, 254, 255, 0.82)`,
    e.stroke(),
    e.restore()
}
function oe(e, t, n) {
    let r = b(t.leadShoulder, t.trailShoulder)
      , i = (t, n, r, i) => {
        let a = e.createLinearGradient(t.x, t.y, n.x, n.y);
        a.addColorStop(0, `rgba(18, 70, 88, 0.98)`),
        a.addColorStop(.48, `rgba(4, 29, 43, 0.98)`),
        a.addColorStop(1, `rgba(21, 82, 101, 0.92)`),
        e.save(),
        e.globalAlpha = i,
        e.lineCap = `round`,
        e.lineJoin = `round`,
        e.beginPath(),
        e.moveTo(t.x, t.y),
        e.lineTo(n.x, n.y),
        e.lineWidth = r,
        e.strokeStyle = a,
        e.stroke(),
        e.restore()
    }
    ;
    i(r, t.neck, 25 * n, .96),
    i(t.neck, t.head, 19 * n, .92)
}
function se(e, t, n, r) {
    if (n <= .01)
        return;
    let i = (1 - n) ** 1.25;
    e.save(),
    e.globalAlpha = i * .72,
    e.strokeStyle = `rgba(87, 229, 247, 0.86)`,
    e.fillStyle = `rgba(25, 166, 190, 0.08)`;
    for (let i = 0; i < 2; i += 1) {
        let a = (10 + i * 9 + n * (29 + i * 10)) * r;
        e.beginPath(),
        e.ellipse(t.x, t.y + 6 * r, a, a * .16, 0, 0, Math.PI * 2),
        e.lineWidth = .56,
        e.stroke(),
        e.fill()
    }
    e.restore()
}
function G(e, t, n, r, i, a, o=1) {
    let s = O(t)
      , c = (T(s.leadLeg, t.leadKnee) + T(s.trailLeg, t.trailKnee)) * .5
      , l = u(c / 44.4, .3, 1.25)
      , d = c * .32
      , f = b(t.leadAnkle, t.trailAnkle, t.leadHip, t.trailHip);
    e.clearRect(0, 0, i, a),
    e.save(),
    e.globalAlpha = .72;
    let p = e.createRadialGradient(f.x, f.y + 10 * l, 4 * l, f.x, f.y + 10 * l, 86 * l);
    p.addColorStop(0, `rgba(9, 70, 94, 0.32)`),
    p.addColorStop(1, `rgba(0, 12, 22, 0)`),
    e.fillStyle = p,
    e.beginPath(),
    e.ellipse(f.x, f.y + 10 * l, 82 * l, 19 * l, .08, 0, Math.PI * 2),
    e.fill(),
    e.restore();
    let m = .62 - n * .08
      , h = s.leadArm
      , g = s.trailArm
      , _ = s.leadLeg
      , v = s.trailLeg
      , y = o > 0;
    se(e, y ? t.trailAnkle : t.leadAnkle, r, l),
    oe(e, t, l),
    U(e, v, t.trailKnee, t.trailAnkle, d * 1.64, d * 1.31, m * .78),
    ae(e, v, t.trailKnee, t.trailAnkle, d * .5, m * .78),
    U(e, _, t.leadKnee, t.leadAnkle, d * 1.76, d * 1.43, m * .92),
    ae(e, _, t.leadKnee, t.leadAnkle, d * .53, m * .92),
    ie(e, t.trailAnkle, t.trailKnee, d * .68, m * .65, y ? r : 0, o),
    ie(e, t.leadAnkle, t.leadKnee, d * .68, m * .72, y ? 0 : r, o),
    W(e, t, l),
    H(e, b(t.leadShoulder, t.trailShoulder), t.neck, d * .95, d * .84, .94),
    H(e, t.neck, t.head, d * .74, d * .86, .97),
    ne(e, t.head, t.neck, 24.8 * l),
    U(e, g, t.trailElbow, t.trailWrist, d * 1.28, d * 1, .9),
    ae(e, g, t.trailElbow, t.trailWrist, d * .39, .9),
    U(e, h, t.leadElbow, t.leadWrist, d * 1.34, d * 1.04, 1),
    ae(e, h, t.leadElbow, t.leadWrist, d * .41, 1),
    re(e, t.trailWrist, t.trailElbow, 7.6 * l),
    re(e, t.leadWrist, t.leadElbow, 8.4 * l)
}
var K = n()
  , ce = [`A`, `B`, `C`, `D`]
  , le = [{
    module: `A`,
    index: `01`,
    eyebrow: `STEREO VISION`,
    title: `双目视觉捕捉`,
    description: `两侧摄像机同步观测足球，以球门线中点为原点建立空间坐标。`
}, {
    module: `B`,
    index: `02`,
    eyebrow: `TRAJECTORY ENGINE`,
    title: `三维轨迹解算`,
    description: `将连续画面转化为实时 X、Y、Z 坐标，还原足球的飞行曲线与入门点。`
}, {
    module: `C`,
    index: `03`,
    eyebrow: `KEEPER SIMULATION`,
    title: `扑救动作仿真`,
    description: `依据落点驱动守门员起跳、横移、触球、落地与回位，呈现完整训练过程。`
}, {
    module: `D`,
    index: `04`,
    eyebrow: `TRAINING LOOP`,
    title: `训练反馈闭环`,
    description: `把轨迹、落点与扑救结果汇总为可复盘的训练信息，支持下一次决策。`
}]
  , ue = 9600
  , de = 2050
  , fe = 2100
  , pe = 2300
  , me = -1.5 * Math.PI / 180
  , he = 150 * Math.PI / 180;
function ge(e, t, n) {
    return {
        x: e.x + (t.x - e.x) * n,
        y: e.y + (t.y - e.y) * n
    }
}
function _e(e, t, n, r) {
    let i = 1 - r;
    return {
        x: i * i * e.x + 2 * i * r * t.x + r * r * n.x,
        y: i * i * e.y + 2 * i * r * t.y + r * r * n.y
    }
}
function ve(e, t, n) {
    let r = [{
        t: 0,
        length: 0
    }]
      , i = e
      , a = 0;
    for (let o = 1; o <= 180; o += 1) {
        let s = o / 180
          , c = _e(e, t, n, s);
        a += Math.hypot(c.x - i.x, c.y - i.y),
        r.push({
            t: s,
            length: a
        }),
        i = c
    }
    return {
        samples: r,
        length: a
    }
}
function ye(e, t) {
    let n = 0
      , r = e.length - 1;
    for (; n < r; ) {
        let i = Math.floor((n + r) / 2);
        e[i].length < t ? n = i + 1 : r = i
    }
    let i = e[n]
      , a = e[Math.max(0, n - 1)]
      , o = i.length - a.length || 1
      , s = (t - a.length) / o;
    return a.t + (i.t - a.t) * s
}
function be(e) {
    let t = J(e);
    return .82 * t + .18 * t * t
}
function xe(e, t) {
    let n = Math.cos(t)
      , r = Math.sin(t);
    return {
        x: e.x * n - e.y * r,
        y: e.x * r + e.y * n
    }
}
function Se(e, t) {
    return e.x * t.x + e.y * t.y
}
function q(e, t, n, r, i) {
    let a = {
        x: Math.cos(me),
        y: Math.sin(me)
    }
      , o = xe(a, he)
      , s = {
        x: r,
        y: n
    }
      , c = Math.max(36, i * .46)
      , l = J(Math.max(i * .34, t * .34), 122, t * .56)
      , u = {
        x: s.x + o.x * l,
        y: s.y + o.y * l
    }
      , d = Math.max(18, i * .04)
      , f = Math.max(c, Math.min((u.x - e * .035) / Math.max(.1, a.x) - d, (e * .965 - u.x) / Math.max(.1, a.x) - d))
      , p = Math.min(i * .7, f)
      , m = {
        x: s.x - a.x * p,
        y: s.y - a.y * p
    }
      , h = {
        x: s.x + a.x * p,
        y: s.y + a.y * p
    }
      , g = {
        x: u.x - a.x * p,
        y: u.y - a.y * p
    }
      , _ = {
        x: u.x + a.x * p,
        y: u.y + a.y * p
    }
      , v = Math.min(26, i * .045)
      , y = Math.min(18, i * .03);
    return {
        goalDirection: a,
        forwardDirection: o,
        goalLineCenter: s,
        penaltyLine: [m, g, _, h],
        penaltySpot: {
            x: s.x + o.x * l * .72,
            y: s.y + o.y * l * .72
        },
        leftCamera: {
            x: g.x - a.x * v + o.x * y,
            y: g.y - a.y * v + o.y * y
        },
        rightCamera: {
            x: _.x + a.x * v + o.x * y,
            y: _.y + a.y * v + o.y * y
        }
    }
}
function J(e, t=0, n=1) {
    return Math.min(n, Math.max(t, e))
}
function Y(e) {
    return 1 - (1 - J(e)) ** 3
}
function X(e) {
    let t = Math.sin(e * 12.9898) * 43758.5453;
    return t - Math.floor(t)
}
var Ce = Array.from({
    length: 144
}, (e, t) => ({
    t: .04 + X(t + 1) * .92,
    radius: .65 + X(t + 17) * 2.1,
    driftX: (X(t + 31) - .5) * 58,
    driftY: (X(t + 47) - .5) * 48,
    delay: X(t + 59) * 660,
    lift: 13 + X(t + 71) * 28
}))
  , we = Array.from({
    length: 500
}, (e, t) => ({
    x: .1 + X(t + 151) * .9,
    y: X(t + 257),
    radius: .34 + X(t + 401) * 1.05,
    alpha: .05 + X(t + 521) * .19,
    rise: 2.2 + X(t + 653) * 5.2,
    sway: 2 + X(t + 787) * 11,
    phase: X(t + 919) * Math.PI * 2
}));
function Te(e, t, n) {
    let r = (e - t) / n;
    return r >= 0 && r <= 1 ? r : -1
}
function Z(e, t, n, r) {
    if (n < 0 || n > 1)
        return;
    let i = (1 - n) ** 1.45 * r;
    e.save(),
    e.globalAlpha = i * .56,
    e.strokeStyle = `rgba(99, 231, 249, 0.82)`,
    e.fillStyle = `rgba(22, 154, 175, 0.06)`;
    for (let r = 0; r < 2; r += 1) {
        let i = 7 + r * 8 + n * (28 + r * 13);
        e.beginPath(),
        e.ellipse(t.x, t.y + 3, i, i * .16, 0, 0, Math.PI * 2),
        e.lineWidth = .56,
        e.stroke(),
        e.fill()
    }
    e.restore()
}
function Ee(e, t, n) {
    e.save(),
    e.strokeStyle = `rgba(200, 251, 255, 0.92)`,
    e.shadowBlur = 0;
    for (let r = 0; r < 3; r += 1) {
        let i = J((n - r * 70) / 620);
        if (i <= 0 || i >= 1)
            continue;
        let a = Y(i)
          , o = 5 + r * 4 + a * (23 + r * 8);
        e.globalAlpha = .6 * (1 - i) ** 2.2,
        e.lineWidth = 1,
        e.beginPath(),
        e.arc(t.x, t.y, o, 0, Math.PI * 2),
        e.stroke()
    }
    e.restore()
}
function De(e, t, n, r=1, i=!1) {
    let a = Math.atan2(n.y - t.y, n.x - t.x);
    e.save(),
    e.translate(t.x, t.y),
    e.rotate(i ? a + Math.PI : a),
    i && (e.scale(-1, 1),
    e.translate(-.55, 0)),
    e.scale(r, r),
    e.globalAlpha = .78,
    e.fillStyle = `rgba(7, 38, 53, 0.94)`,
    e.strokeStyle = `rgba(168, 243, 253, 0.78)`,
    e.lineWidth = .68,
    e.beginPath(),
    e.moveTo(-6.6, -3.8),
    e.lineTo(4.8, -3.8),
    e.lineTo(6.6, -1.7),
    e.lineTo(6.6, 2.8),
    e.lineTo(4.1, 3.8),
    e.lineTo(-6.6, 3.8),
    e.closePath(),
    e.fill(),
    e.stroke(),
    e.beginPath(),
    e.arc(5.4, -.05, 2.15, 0, Math.PI * 2),
    e.fillStyle = `rgba(213, 255, 255, 0.94)`,
    e.fill(),
    e.beginPath(),
    e.arc(5.4, -.05, 1.02, 0, Math.PI * 2),
    e.fillStyle = `#00f2fe`,
    e.shadowBlur = 7,
    e.shadowColor = `rgba(0, 242, 254, 0.96)`,
    e.fill(),
    e.shadowBlur = 0,
    e.beginPath(),
    e.moveTo(-1.8, 3.8),
    e.lineTo(-3.3, 7.4),
    e.moveTo(-1.8, 3.8),
    e.lineTo(1.3, 7.4),
    e.lineWidth = .58,
    e.strokeStyle = `rgba(128, 221, 237, 0.66)`,
    e.stroke(),
    e.restore()
}
function Oe(e, t, n, r, i, a, o, s, c, l) {
    e.clearRect(0, 0, t, n);
    let u = J(o, n * .39, n * .72)
      , d = n + 32
      , f = Te(i, de, 980)
      , p = q(t, n, o, s, c)
      , {penaltyLine: m, goalLineCenter: h} = p
      , g = a
      , _ = Math.max(t, n) * .62
      , v = e.createRadialGradient(s, o - c * .17, 0, s, o - c * .17, _);
    v.addColorStop(0, `rgba(0, 242, 254, 0.14)`),
    v.addColorStop(.32, `rgba(0, 213, 236, 0.075)`),
    v.addColorStop(.62, `rgba(0, 157, 190, 0.024)`),
    v.addColorStop(1, `rgba(0, 242, 254, 0)`),
    e.save(),
    e.globalAlpha = .95 + Math.sin(r * 25e-5) * .045,
    e.fillStyle = v,
    e.fillRect(0, 0, t, n),
    e.restore();
    let y = e.createLinearGradient(0, u - n * .24, 0, d);
    y.addColorStop(0, `rgba(4, 7, 17, 0.76)`),
    y.addColorStop(.48, `rgba(4, 12, 23, 0.18)`),
    y.addColorStop(1, `rgba(4, 7, 17, 0)`),
    e.save(),
    e.fillStyle = y,
    e.fillRect(0, 0, t, n),
    e.restore(),
    e.save(),
    e.globalAlpha = .3,
    e.strokeStyle = `rgba(38, 191, 211, 0.34)`,
    e.lineWidth = .58,
    e.setLineDash([.9, 5.2]),
    e.lineDashOffset = -r * .0018;
    for (let n = 1; n <= 9; n += 1) {
        let r = n / 9
          , i = u + (d - u) * r * r
          , a = t * .11 * (1 - r);
        e.beginPath(),
        e.moveTo(a, i),
        e.lineTo(t - a, i),
        e.stroke()
    }
    for (let n = -10; n <= 10; n += 1) {
        let r = s + n * t * .096;
        e.beginPath(),
        e.moveTo(s + n * t * .012, u),
        e.lineTo(r, d),
        e.stroke()
    }
    e.setLineDash([]),
    e.globalAlpha = .33,
    e.lineWidth = .86,
    e.strokeStyle = `rgba(113, 225, 242, 0.82)`,
    e.beginPath(),
    e.moveTo(m[0].x, m[0].y),
    m.slice(1).forEach(t => e.lineTo(t.x, t.y)),
    e.lineTo(m[0].x, m[0].y),
    e.stroke(),
    e.globalAlpha = .66,
    e.fillStyle = `rgba(229, 255, 255, 0.96)`,
    e.shadowBlur = 9,
    e.shadowColor = `rgba(0, 242, 254, 0.88)`,
    e.beginPath(),
    e.arc(g.x, g.y, 2.4, 0, Math.PI * 2),
    e.fill(),
    e.shadowBlur = 0,
    e.beginPath(),
    e.arc(g.x, g.y, 6.3, 0, Math.PI * 2),
    e.lineWidth = .54,
    e.strokeStyle = `rgba(88, 218, 238, 0.46)`,
    e.stroke();
    let b = p.leftCamera
      , x = p.rightCamera;
    if (e.globalAlpha = .44,
    e.lineWidth = .7,
    e.strokeStyle = `rgba(118, 231, 248, 0.72)`,
    e.setLineDash([1.2, 2.2]),
    e.beginPath(),
    e.moveTo(b.x, b.y),
    e.lineTo(x.x, x.y),
    e.stroke(),
    e.setLineDash([]),
    f >= 0) {
        let t = Y(f)
          , n = ge(b, x, Math.max(0, t - .13))
          , r = ge(b, x, t)
          , i = e.createLinearGradient(n.x, n.y, r.x, r.y);
        i.addColorStop(0, `rgba(0, 242, 254, 0)`),
        i.addColorStop(.55, `rgba(0, 242, 254, 0.8)`),
        i.addColorStop(1, `rgba(236, 255, 255, 1)`),
        e.save(),
        e.globalAlpha = .98,
        e.strokeStyle = i,
        e.lineWidth = 2.15,
        e.lineCap = `round`,
        e.shadowBlur = 16,
        e.shadowColor = `rgba(0, 242, 254, 1)`,
        e.beginPath(),
        e.moveTo(n.x, n.y),
        e.lineTo(r.x, r.y),
        e.stroke(),
        e.restore()
    }
    let S = {
        x: h.x + p.forwardDirection.x * 8,
        y: h.y + p.forwardDirection.y * 8
    }
      , C = J(c / 550, 1.48, 1.64);
    De(e, b, S, C),
    De(e, x, S, C, !0),
    f >= 0 && (e.globalAlpha = (1 - f) ** 1.08 * .92,
    e.beginPath(),
    e.arc(a.x, a.y, 4 + f * 8, 0, Math.PI * 2),
    e.lineWidth = .62,
    e.strokeStyle = `rgba(121, 231, 249, 0.78)`,
    e.stroke()),
    e.restore();
    let w = t < 720 ? 330 : we.length;
    e.save(),
    e.fillStyle = `rgba(194, 249, 255, 0.92)`;
    for (let i = 0; i < w; i += 1) {
        let a = we[i]
          , o = r * 7e-5
          , s = t * a.x + Math.sin(o * 1.9 + a.phase) * a.sway
          , c = u + (a.y + o * a.rise * .08) % 1 * Math.max(42, n - u)
          , l = a.alpha * (.64 + Math.sin(o * 3 + a.phase) * .16);
        e.globalAlpha = Math.max(0, l),
        e.beginPath(),
        e.arc(s, c, a.radius, 0, Math.PI * 2),
        e.fill()
    }
    e.restore(),
    Z(e, a, Te(i, de, 820), .92),
    Z(e, l, Te(i, 2520, 780), .62)
}
function ke({className: e=``, showCoordinates: t=!1, frameRef: n, rippleRef: r}) {
    return (0,
    K.jsxs)(`div`, {
        ref: n,
        className: `goal-frame ${e}`,
        children: [(0,
        K.jsx)(`span`, {
            className: `goal-top`
        }), (0,
        K.jsx)(`span`, {
            className: `goal-post goal-post-left`
        }), (0,
        K.jsx)(`span`, {
            className: `goal-post goal-post-right`
        }), (0,
        K.jsx)(`span`, {
            className: `goal-ground`
        }), (0,
        K.jsx)(`span`, {
            className: `goal-net`
        }), (0,
        K.jsx)(`span`, {
            ref: r,
            className: `goal-net-ripple`
        }), (0,
        K.jsx)(`span`, {
            className: `goal-back-top`
        }), (0,
        K.jsx)(`span`, {
            className: `goal-back-post goal-back-post-left`
        }), (0,
        K.jsx)(`span`, {
            className: `goal-back-post goal-back-post-right`
        }), (0,
        K.jsx)(`span`, {
            className: `goal-net-back`
        }), (0,
        K.jsx)(`span`, {
            className: `goal-net-roof`
        }), (0,
        K.jsx)(`span`, {
            className: `goal-net-floor`
        }), (0,
        K.jsx)(`span`, {
            className: `goal-node goal-node-one`
        }), (0,
        K.jsx)(`span`, {
            className: `goal-node goal-node-two`
        }), (0,
        K.jsx)(`span`, {
            className: `goal-node goal-node-three`
        }), (0,
        K.jsx)(`span`, {
            className: `goal-node goal-node-four`
        }), t ? (0,
        K.jsx)(Ae, {}) : null]
    })
}
function Ae() {
    return (0,
    K.jsxs)(`div`, {
        className: `goal-coordinate-system`,
        "aria-hidden": `true`,
        children: [(0,
        K.jsxs)(`span`, {
            className: `goal-axis goal-axis-x`,
            children: [(0,
            K.jsx)(`b`, {
                className: `axis-label-negative`,
                children: `−X`
            }), (0,
            K.jsx)(`b`, {
                className: `axis-label-positive`,
                children: `+X`
            })]
        }), (0,
        K.jsx)(`span`, {
            className: `goal-axis goal-axis-y`,
            children: (0,
            K.jsx)(`b`, {
                children: `+Y`
            })
        }), (0,
        K.jsx)(`span`, {
            className: `goal-axis goal-axis-z`,
            children: (0,
            K.jsx)(`b`, {
                children: `+Z`
            })
        }), (0,
        K.jsx)(`span`, {
            className: `goal-axis-origin`
        })]
    })
}
function Q({className: e=``, canvasRef: t}) {
    return (0,
    K.jsx)(`div`, {
        className: `keeper-rig ${e}`,
        "aria-hidden": `true`,
        children: t ? (0,
        K.jsx)(`canvas`, {
            ref: t,
            className: `keeper-rig-canvas`
        }) : (0,
        K.jsxs)(`div`, {
            className: `keeper-static-silhouette`,
            children: [(0,
            K.jsx)(`span`, {
                className: `keeper-static-head`
            }), (0,
            K.jsx)(`span`, {
                className: `keeper-static-body`
            }), (0,
            K.jsx)(`span`, {
                className: `keeper-static-arm keeper-static-arm-lead`
            }), (0,
            K.jsx)(`span`, {
                className: `keeper-static-arm keeper-static-arm-trail`
            }), (0,
            K.jsx)(`span`, {
                className: `keeper-static-leg keeper-static-leg-lead`
            }), (0,
            K.jsx)(`span`, {
                className: `keeper-static-leg keeper-static-leg-trail`
            }), (0,
            K.jsx)(`span`, {
                className: `keeper-static-core`
            })]
        })
    })
}
function je() {
    let[e,t] = (0,
    r.useState)(`launch`)
      , [n,i] = (0,
    r.useState)(null)
      , [a,o] = (0,
    r.useState)(!1)
      , [s,c] = (0,
    r.useState)(!1)
      , l = (0,
    r.useRef)(null)
      , u = (0,
    r.useRef)(null)
      , d = (0,
    r.useRef)(null)
      , f = (0,
    r.useRef)(null)
      , p = (0,
    r.useRef)(null)
      , m = (0,
    r.useRef)(null)
      , h = (0,
    r.useRef)(null)
      , g = (0,
    r.useRef)(null)
      , _ = (0,
    r.useRef)(null)
      , v = (0,
    r.useRef)(null)
      , y = () => {
        let e = window.matchMedia(`(prefers-reduced-motion: reduce)`).matches;
        u.current?.scrollIntoView({
            behavior: e ? `auto` : `smooth`,
            block: `start`
        })
    }
      , b = () => {
        o(!1),
        t(`training`),
        window.requestAnimationFrame( () => {
            window.scrollTo({
                top: 0,
                behavior: `auto`
            })
        }
        )
    }
    ;
    return (0,
    r.useEffect)( () => {
        let e = window.matchMedia(`(max-width: 560px)`)
          , t = () => c(e.matches);
        return t(),
        e.addEventListener(`change`, t),
        () => e.removeEventListener(`change`, t)
    }
    , []),
    (0,
    r.useEffect)( () => {
        if (e !== `launch`)
            return;
        let t = l.current
          , n = u.current;
        if (!t || !n || typeof IntersectionObserver > `u`)
            return;
        let r = Array.from(n.querySelectorAll(`[data-story-reveal]`));
        n.classList.add(`has-reveal-observer`);
        let i = new IntersectionObserver(e => {
            for (let n of e) {
                if (n.target === t) {
                    o(!n.isIntersecting);
                    continue
                }
                n.isIntersecting && (n.target.classList.add(`is-visible`),
                i.unobserve(n.target))
            }
        }
        ,{
            threshold: .15
        });
        return i.observe(t),
        r.forEach(e => i.observe(e)),
        () => {
            i.disconnect(),
            n.classList.remove(`has-reveal-observer`)
        }
    }
    , [e]),
    (0,
    r.useEffect)( () => {
        if (e !== `launch`)
            return;
        let t = d.current
          , n = p.current
          , r = m.current
          , i = h.current
          , a = g.current
          , o = _.current
          , s = v.current
          , c = n?.getContext(`2d`)
          , l = r?.getContext(`2d`)
          , u = i?.getContext(`2d`)
          , y = window.matchMedia(`(prefers-reduced-motion: reduce)`).matches
          , b = new URLSearchParams(window.location.search).get(`keeperFrame`)
          , x = b === null ? null : Number(b)
          , S = x !== null && Number.isFinite(x) ? J(x, 0, ue - 1) : null;
        if (!t || !n || !r || !i || !a || !o || !s || !c || !l || !u)
            return;
        let C = {
            x: t.querySelector(`[data-coordinate="x"]`),
            y: t.querySelector(`[data-coordinate="y"]`),
            z: t.querySelector(`[data-coordinate="z"]`)
        }
          , w = t.querySelector(`[data-crossing-coordinate]`)
          , T = 0
          , E = null
          , D = 0
          , O = 0
          , k = 0
          , A = {
            x: 0,
            y: 0
        }
          , j = {
            x: 0,
            y: 0
        }
          , M = {
            x: 0,
            y: 0
        }
          , N = 0
          , P = 0
          , F = 0
          , I = 0
          , L = {
            x: 0,
            y: 0,
            z: 0
        }
          , R = null
          , z = 0
          , B = e => {
            let a = t.getBoundingClientRect()
              , d = Math.max(1, Math.round(a.width))
              , p = Math.max(1, Math.round(a.height))
              , m = Math.min(window.devicePixelRatio || 1, 2)
              , h = d !== D || p !== O || m !== k;
            if (!h && !e)
                return;
            h && (D = d,
            O = p,
            k = m,
            n.width = Math.round(D * k),
            n.height = Math.round(O * k),
            c.setTransform(k, 0, 0, k, 0, 0),
            r.width = Math.round(D * k),
            r.height = Math.round(O * k),
            l.setTransform(k, 0, 0, k, 0, 0),
            i.width = Math.round(D * k),
            i.height = Math.round(O * k),
            u.setTransform(k, 0, 0, k, 0, 0),
            z = 0);
            let g = f.current?.getBoundingClientRect()
              , _ = Math.min(D * .49, 896)
              , v = O * .25 + _ / 3 * .88;
            if (g) {
                let e = g.left - a.left;
                N = g.top - a.top + g.height * .88,
                P = e + g.width * .5,
                F = g.width,
                I = g.height * .84
            } else
                N = v,
                P = D * .65,
                F = _,
                I = _ / 3 * .84;
            let y = q(D, O, N, P, F);
            A = y.penaltySpot;
            let b = {
                x: Math.sin(me),
                y: -Math.cos(me)
            }
              , x = F * .46;
            M = {
                x: y.goalLineCenter.x + y.goalDirection.x * x * .76 + b.x * I * .78,
                y: y.goalLineCenter.y + y.goalDirection.y * x * .76 + b.y * I * .78
            },
            j = {
                x: A.x + (M.x - A.x) * .5 - y.goalDirection.x * F * .025,
                y: Math.max(O * .04, A.y + (M.y - A.y) * .38 - Math.max(O * .17, F * .13))
            };
            let S = {
                x: M.x - y.goalLineCenter.x,
                y: M.y - y.goalLineCenter.y
            };
            L = {
                x: J(Se(S, y.goalDirection) / Math.max(1, x) * 3.66, -3.66, 3.66),
                y: 0,
                z: J(Se(S, b) / Math.max(1, I) * 2.44, .1, 2.44)
            },
            R = ve(A, j, M),
            o.style.setProperty(`--crossing-x`, `${M.x}px`),
            o.style.setProperty(`--crossing-y`, `${M.y}px`),
            s.style.setProperty(`--net-hit-x`, `${J(.5 + Se(S, y.goalDirection) / Math.max(1, x * 2)) * 100}%`),
            s.style.setProperty(`--net-hit-y`, `${J(1 - Se(S, b) / Math.max(1, I)) * 100}%`)
        }
          , V = e => {
            E === null && (E = e);
            let t = S ?? (y ? 0 : e - E);
            if (B(t < de - 120),
            !R) {
                T = window.requestAnimationFrame(V);
                return
            }
            let n = t % ue;
            n < z && o.classList.remove(`is-crossed`),
            z = n;
            let r = Math.max(0, (n - de) / fe)
              , i = be(Math.min(r, 1))
              , d = R.length * i
              , f = ye(R.samples, d)
              , p = ge(A, j, f)
              , m = ge(p, ge(j, M, f), f)
              , h = L.x * f
              , g = 11 * (1 - i)
              , _ = Math.min(2.62, L.z + .68)
              , v = .1 * (1 - f) * (1 - f) + 2 * (1 - f) * f * _ + f * f * L.z;
            C.x && (C.x.textContent = `${h >= 0 ? `+` : ``}${h.toFixed(3)} m`),
            C.y && (C.y.textContent = `${g.toFixed(3)} m`),
            C.z && (C.z.textContent = `${v.toFixed(3)} m`),
            w && (w.textContent = `P · X ${L.x >= 0 ? `+` : ``}${L.x.toFixed(3)} · Y +0.000 · Z +${L.z.toFixed(3)} m`),
            o.classList.toggle(`is-crossed`, r > 1),
            s.classList.toggle(`is-active`, r > 1);
            let b = J(R.length * .16, 68, 150)
              , x = Math.max(0, d - b)
              , k = Math.min(1, r / .045)
              , I = Math.max(0, n - de - fe)
              , te = J(I / pe)
              , H = r < 1 ? k : (1 - te) ** 1.45
              , U = r < 1 ? k : Math.max(0, 1 - I / 95)
              , W = ee(D, O, M, n, ue, N, P, F);
            if (Oe(c, D, O, y ? 0 : e, n, A, N, P, F, W.pushAnchor),
            l.clearRect(0, 0, D, O),
            G(u, W.pose, W.landingProgress, W.pushProgress, D, O, W.diveDirection),
            r >= 0) {
                l.save(),
                l.globalAlpha = H * .22,
                l.lineCap = `round`,
                l.lineJoin = `round`,
                l.shadowBlur = 8,
                l.shadowColor = `rgba(0, 242, 254, 0.72)`,
                l.strokeStyle = `rgba(95, 223, 249, 0.68)`,
                l.lineWidth = .72,
                l.beginPath(),
                l.moveTo(A.x, A.y),
                l.quadraticCurveTo(p.x, p.y, m.x, m.y),
                l.stroke(),
                l.shadowBlur = 0,
                l.globalAlpha = H * .36,
                l.strokeStyle = `rgba(232, 254, 255, 0.88)`,
                l.lineWidth = .52,
                l.beginPath(),
                l.moveTo(A.x, A.y),
                l.quadraticCurveTo(p.x, p.y, m.x, m.y),
                l.stroke(),
                l.globalAlpha = H * .2,
                l.strokeStyle = `rgba(119, 231, 255, 0.82)`,
                l.lineWidth = .36,
                l.setLineDash([1, 5.8]),
                l.lineDashOffset = -e * .024,
                l.beginPath(),
                l.moveTo(A.x, A.y),
                l.quadraticCurveTo(p.x, p.y, m.x, m.y),
                l.stroke(),
                l.setLineDash([]),
                l.lineCap = `round`,
                l.strokeStyle = `rgba(226, 254, 255, 0.98)`,
                l.shadowColor = `rgba(56, 226, 255, 0.96)`;
                let t = Math.max(1, d - x);
                for (let e = 0; e < 28; e += 1) {
                    let n = e / 28
                      , r = (e + 1) / 28
                      , i = ye(R.samples, x + t * n)
                      , a = ye(R.samples, x + t * r)
                      , o = _e(A, j, M, i)
                      , s = _e(A, j, M, a)
                      , c = r ** 3.2;
                    l.globalAlpha = H * (.008 + c * .992),
                    l.lineWidth = .28 + c * 4.1,
                    l.shadowBlur = .5 + c * 25.5,
                    l.beginPath(),
                    l.moveTo(o.x, o.y),
                    l.lineTo(s.x, s.y),
                    l.stroke()
                }
                l.shadowBlur = 0,
                l.restore()
            }
            if (I > 0) {
                l.save(),
                l.shadowBlur = 3.5,
                l.shadowColor = `rgba(108, 232, 255, 0.62)`;
                for (let e of Ce) {
                    let t = J((I - e.delay) / 1500);
                    if (t <= 0 || t >= 1)
                        continue;
                    let n = _e(A, j, M, e.t)
                      , r = .38 + t * .9
                      , i = n.x + e.driftX * r * t
                      , a = n.y + e.driftY * r * t - e.lift * t ** 1.24
                      , o = (1 - t) ** 1.22 * .86
                      , s = e.radius * (.62 + t * 2.25);
                    l.globalAlpha = o,
                    l.lineWidth = .56,
                    l.strokeStyle = `rgba(190, 249, 255, 0.92)`,
                    l.fillStyle = `rgba(96, 223, 252, 0.36)`,
                    l.beginPath(),
                    l.arc(i, a, s, 0, Math.PI * 2),
                    l.fill(),
                    l.stroke()
                }
                l.restore(),
                Ee(l, M, I)
            }
            a.style.transform = `translate3d(${m.x}px, ${m.y}px, 0) translate(-50%, -50%)`,
            a.style.opacity = `${U}`,
            y || (T = window.requestAnimationFrame(V))
        }
        ;
        return T = window.requestAnimationFrame(V),
        () => window.cancelAnimationFrame(T)
    }
    , [e]),
    (0,
    K.jsxs)(`main`, {
        className: `experience ${e === `training` ? `is-training` : ``}`,
        children: [(0,
        K.jsx)(`div`, {
            className: `ambient ambient-one`,
            "aria-hidden": `true`
        }), (0,
        K.jsx)(`div`, {
            className: `ambient ambient-two`,
            "aria-hidden": `true`
        }), (0,
        K.jsx)(`div`, {
            className: `precision-grid`,
            "aria-hidden": `true`
        }), e === `launch` ? (0,
        K.jsxs)(K.Fragment, {
            children: [(0,
            K.jsxs)(`section`, {
                className: `launch-screen`,
                "aria-labelledby": `launch-title`,
                ref: l,
                children: [(0,
                K.jsxs)(`div`, {
                    className: `launch-copy`,
                    children: [(0,
                    K.jsx)(`p`, {
                        className: `eyebrow`,
                        children: `AR × AI`
                    }), (0,
                    K.jsx)(`h1`, {
                        id: `launch-title`,
                        children: `点球训练系统`
                    }), (0,
                    K.jsx)(`p`, {
                        className: `english-title`,
                        children: `AR × AI PENALTY TRAINING`
                    }), (0,
                    K.jsx)(`p`, {
                        className: `launch-intro`,
                        children: `以更自然的判断，进入下一次射门。`
                    }), (0,
                    K.jsxs)(`div`, {
                        className: `launch-actions`,
                        children: [(0,
                        K.jsxs)(`button`, {
                            className: `enter-button`,
                            type: `button`,
                            onClick: y,
                            children: [(0,
                            K.jsx)(`span`, {
                                children: `项目详情`
                            }), (0,
                            K.jsx)(`span`, {
                                className: `button-arrow`,
                                "aria-hidden": `true`,
                                children: `→`
                            })]
                        }), (0,
                        K.jsxs)(`button`, {
                            className: `enter-button`,
                            type: `button`,
                            onClick: b,
                            children: [(0,
                            K.jsx)(`span`, {
                                children: `进入训练`
                            }), (0,
                            K.jsx)(`span`, {
                                className: `button-arrow`,
                                "aria-hidden": `true`,
                                children: `→`
                            })]
                        })]
                    })]
                }), (0,
                K.jsxs)(`div`, {
                    className: `launch-visual`,
                    ref: d,
                    "aria-hidden": `true`,
                    children: [(0,
                    K.jsx)(`div`, {
                        className: `stadium-stands`
                    }), (0,
                    K.jsx)(`div`, {
                        className: `rim-ray rim-ray-left`
                    }), (0,
                    K.jsx)(`div`, {
                        className: `rim-ray rim-ray-right`
                    }), (0,
                    K.jsx)(`canvas`, {
                        className: `atmosphere-canvas`,
                        ref: p
                    }), (0,
                    K.jsx)(`div`, {
                        className: `floor-fog`
                    }), (0,
                    K.jsx)(ke, {
                        className: `hero-goal`,
                        showCoordinates: !0,
                        frameRef: f,
                        rippleRef: v
                    }), (0,
                    K.jsx)(`div`, {
                        className: `beam beam-one`
                    }), (0,
                    K.jsx)(`div`, {
                        className: `beam beam-two`
                    }), (0,
                    K.jsx)(`div`, {
                        className: `beam beam-three`
                    }), (0,
                    K.jsx)(`div`, {
                        className: `trajectory-decoration`
                    }), (0,
                    K.jsx)(`canvas`, {
                        className: `flight-track`,
                        ref: m
                    }), (0,
                    K.jsx)(`div`, {
                        className: `ball-system`,
                        ref: g,
                        children: (0,
                        K.jsx)(`span`, {
                            className: `data-ball`,
                            children: (0,
                            K.jsx)(`i`, {})
                        })
                    }), (0,
                    K.jsxs)(`div`, {
                        className: `goal-crossing-marker`,
                        ref: _,
                        children: [(0,
                        K.jsx)(`span`, {
                            className: `goal-crossing-dot`
                        }), (0,
                        K.jsx)(`output`, {
                            "data-crossing-coordinate": !0,
                            children: `P · X +2.010 · Y 0.000 · Z +2.100 m`
                        })]
                    }), (0,
                    K.jsx)(Q, {
                        className: `hero-keeper`,
                        canvasRef: h
                    }), (0,
                    K.jsx)(`div`, {
                        className: `floor-fog floor-fog-foreground`
                    }), (0,
                    K.jsxs)(`aside`, {
                        className: `goal-telemetry`,
                        "aria-label": `足球实时坐标`,
                        children: [(0,
                        K.jsx)(`p`, {
                            children: `GOAL LINE / O`
                        }), (0,
                        K.jsxs)(`dl`, {
                            children: [(0,
                            K.jsxs)(`div`, {
                                children: [(0,
                                K.jsx)(`dt`, {
                                    children: `X`
                                }), (0,
                                K.jsx)(`dd`, {
                                    children: (0,
                                    K.jsx)(`output`, {
                                        "data-coordinate": `x`,
                                        children: `-3.350 m`
                                    })
                                })]
                            }), (0,
                            K.jsxs)(`div`, {
                                children: [(0,
                                K.jsx)(`dt`, {
                                    children: `Y`
                                }), (0,
                                K.jsx)(`dd`, {
                                    children: (0,
                                    K.jsx)(`output`, {
                                        "data-coordinate": `y`,
                                        children: `11.000 m`
                                    })
                                })]
                            }), (0,
                            K.jsxs)(`div`, {
                                children: [(0,
                                K.jsx)(`dt`, {
                                    children: `Z`
                                }), (0,
                                K.jsx)(`dd`, {
                                    children: (0,
                                    K.jsx)(`output`, {
                                        "data-coordinate": `z`,
                                        children: `0.100 m`
                                    })
                                })]
                            })]
                        })]
                    })]
                }), (0,
                K.jsxs)(`p`, {
                    className: `launch-caption`,
                    children: [`PRECISE STRIKE `, (0,
                    K.jsx)(`span`, {
                        children: `·`
                    }), ` NATURAL RESPONSE`]
                }), (0,
                K.jsx)(`button`, {
                    className: `scroll-indicator`,
                    type: `button`,
                    "aria-label": `查看项目详情`,
                    "aria-controls": `project-story`,
                    onClick: y,
                    children: (0,
                    K.jsx)(`span`, {
                        "aria-hidden": `true`
                    })
                })]
            }), (0,
            K.jsxs)(`section`, {
                className: `details-screen story-section`,
                id: `project-story`,
                "aria-labelledby": `details-title`,
                ref: u,
                children: [(0,
                K.jsxs)(`div`, {
                    className: `story-intro`,
                    children: [(0,
                    K.jsx)(`p`, {
                        className: `eyebrow`,
                        children: `PROJECT OVERVIEW`
                    }), (0,
                    K.jsx)(`h2`, {
                        id: `details-title`,
                        children: `从观察，到下一次扑救。`
                    }), (0,
                    K.jsx)(`p`, {
                        children: `四个连续环节，把双目视觉、空间解算与训练反馈连接成一个完整系统。`
                    })]
                }), (0,
                K.jsx)(`div`, {
                    className: `detail-grid story-grid`,
                    "aria-label": `项目核心板块`,
                    children: le.map(e => (0,
                    K.jsxs)(`article`, {
                        className: `detail-card story-card`,
                        "data-story-reveal": !0,
                        children: [(0,
                        K.jsxs)(`div`, {
                            className: `story-card-copy`,
                            children: [(0,
                            K.jsx)(`span`, {
                                className: `detail-index`,
                                children: e.index
                            }), (0,
                            K.jsx)(`p`, {
                                className: `story-card-eyebrow`,
                                children: e.eyebrow
                            }), (0,
                            K.jsx)(`h3`, {
                                children: e.title
                            }), (0,
                            K.jsx)(`p`, {
                                children: e.description
                            })]
                        }), (0,
                        K.jsxs)(`div`, {
                            className: `story-card-mark`,
                            "aria-hidden": `true`,
                            children: [(0,
                            K.jsx)(`span`, {
                                className: `detail-number`,
                                children: e.module
                            }), (0,
                            K.jsx)(`span`, {
                                className: `detail-line`
                            })]
                        })]
                    }, e.module))
                }), (0,
                K.jsxs)(`div`, {
                    className: `details-visual`,
                    "aria-hidden": `true`,
                    children: [(0,
                    K.jsx)(ke, {
                        className: `details-goal`
                    }), (0,
                    K.jsx)(Q, {
                        className: `details-keeper`
                    })]
                })]
            }), (0,
            K.jsxs)(`button`, {
                className: `sticky-training-cta ${a || s ? `is-visible` : ``}`,
                type: `button`,
                "aria-hidden": !(a || s),
                tabIndex: a || s ? 0 : -1,
                onClick: b,
                children: [(0,
                K.jsx)(`span`, {
                    children: `进入训练`
                }), (0,
                K.jsx)(`span`, {
                    "aria-hidden": `true`,
                    children: `→`
                })]
            })]
        }) : (0,
        K.jsxs)(`section`, {
            className: `training-screen`,
            "aria-labelledby": `training-title`,
            children: [(0,
            K.jsxs)(`header`, {
                className: `training-header`,
                children: [(0,
                K.jsxs)(`div`, {
                    className: `brand-lockup`,
                    children: [(0,
                    K.jsx)(`p`, {
                        children: `AR × AI`
                    }), (0,
                    K.jsx)(`span`, {
                        children: `点球训练系统`
                    })]
                }), (0,
                K.jsxs)(`button`, {
                    className: `back-button`,
                    type: `button`,
                    onClick: () => {
                        i(null),
                        t(`launch`),
                        window.requestAnimationFrame( () => {
                            window.scrollTo({
                                top: 0,
                                behavior: `auto`
                            })
                        }
                        )
                    }
                    ,
                    children: [(0,
                    K.jsx)(`span`, {
                        "aria-hidden": `true`,
                        children: `←`
                    }), `返回启动页`]
                })]
            }), (0,
            K.jsxs)(`div`, {
                className: `training-content`,
                children: [(0,
                K.jsxs)(`div`, {
                    className: `training-heading`,
                    children: [(0,
                    K.jsx)(`p`, {
                        className: `eyebrow`,
                        children: `TRAINING MODULES`
                    }), (0,
                    K.jsx)(`h2`, {
                        id: `training-title`,
                        children: `选择训练模块`
                    }), (0,
                    K.jsx)(`p`, {
                        children: `从一个入口开始，建立更专注的点球训练体验。`
                    })]
                }), (0,
                K.jsx)(`div`, {
                    className: `module-grid`,
                    role: `group`,
                    "aria-label": `训练模块`,
                    children: ce.map(e => (0,
                    K.jsxs)(`button`, {
                        className: `module-card ${n === e ? `is-active` : ``}`,
                        type: `button`,
                        "aria-pressed": n === e,
                        onClick: () => i(e),
                        children: [(0,
                        K.jsx)(`span`, {
                            className: `module-letter`,
                            children: e
                        }), (0,
                        K.jsx)(`span`, {
                            className: `module-line`,
                            "aria-hidden": `true`
                        })]
                    }, e))
                })]
            }), (0,
            K.jsxs)(`div`, {
                className: `training-visual`,
                "aria-hidden": `true`,
                children: [(0,
                K.jsx)(ke, {
                    className: `training-goal`
                }), (0,
                K.jsx)(Q, {
                    className: `training-keeper`
                })]
            })]
        })]
    })
}
export {je as default};
