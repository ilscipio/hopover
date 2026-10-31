// Hopover meeting point: exchanges addresses only, never forwards file data.
// HTTP behind a reverse proxy that sets X-Real-IP and X-Real-Port.
package main

import (
	"encoding/json"
	"io"
	"net"
	"net/http"
	"os"
	"os/signal"
	"regexp"
	"sync"
	"syscall"
	"time"
)

const (
	maxBody    = 2048
	maxRooms   = 1000
	perIPLimit = 20
)

var roomRe = regexp.MustCompile(`^[0-9a-f]{64}$`)

type req struct {
	V     int      `json:"v"`
	Op    string   `json:"op"`
	Room  string   `json:"room"`
	Local []string `json:"local"`
}

type waiter struct {
	op    string
	you   string
	local []string
	ch    chan map[string]any
}

type server struct {
	mu    sync.Mutex
	rooms map[string]*waiter
	hits  map[string][]time.Time
	ttl   time.Duration

	matches   map[string]*matched
	smu       sync.Mutex
	stats     statsData
	dirty     bool
	statsPath string
}

func newServer(ttl time.Duration) *server {
	return &server{rooms: map[string]*waiter{}, hits: map[string][]time.Time{}, ttl: ttl,
		matches: map[string]*matched{}, stats: statsData{Days: map[string]counts{}}}
}

func reply(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	json.NewEncoder(w).Encode(v)
}

func errv(e string) map[string]any { return map[string]any{"v": 1, "error": e} }

// trusted: the peer is the proxy (loopback or a private/bridge address).
func trusted(r *http.Request) bool {
	h, _, _ := net.SplitHostPort(r.RemoteAddr)
	ip := net.ParseIP(h)
	return ip != nil && (ip.IsLoopback() || ip.IsPrivate())
}

// addr returns the client address: proxy headers if trusted, else the TCP source.
func addr(r *http.Request) (ip, full string) {
	if trusted(r) {
		if i, p := r.Header.Get("X-Real-IP"), r.Header.Get("X-Real-Port"); net.ParseIP(i) != nil && p != "" {
			return i, net.JoinHostPort(i, p)
		}
	}
	h, _, _ := net.SplitHostPort(r.RemoteAddr)
	return h, r.RemoteAddr
}

// allow keeps timestamps per IP in memory only.
func (s *server) allow(ip string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	now := time.Now()
	var keep []time.Time
	for _, t := range s.hits[ip] {
		if now.Sub(t) < time.Minute {
			keep = append(keep, t)
		}
	}
	if len(keep) >= perIPLimit {
		s.hits[ip] = keep
		return false
	}
	s.hits[ip] = append(keep, now)
	return true
}

func (s *server) sweep() {
	for range time.Tick(time.Minute) {
		s.mu.Lock()
		now := time.Now()
		for ip, ts := range s.hits {
			if len(ts) == 0 || now.Sub(ts[len(ts)-1]) > time.Minute {
				delete(s.hits, ip)
			}
		}
		for r, m := range s.matches {
			if now.Sub(m.at) > resultWindow {
				delete(s.matches, r)
			}
		}
		s.mu.Unlock()
	}
}

func (s *server) ping(w http.ResponseWriter, r *http.Request) {
	ip, full := addr(r)
	if !s.allow(ip) {
		reply(w, 429, errv("rate_limited"))
		return
	}
	reply(w, 200, map[string]any{"v": 1, "pong": true, "you": full})
}

func (s *server) meet(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		reply(w, 405, errv("bad_request"))
		return
	}
	ip, full := addr(r)
	if !s.allow(ip) {
		reply(w, 429, errv("rate_limited"))
		return
	}
	var q req
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, maxBody))
	if err != nil || json.Unmarshal(body, &q) != nil || q.V != 1 {
		reply(w, 400, errv("bad_request"))
		return
	}
	if q.Op == "ping" {
		reply(w, 200, map[string]any{"v": 1, "pong": true, "you": full})
		return
	}
	if (q.Op != "host" && q.Op != "join") || !roomRe.MatchString(q.Room) {
		reply(w, 400, errv("bad_request"))
		return
	}
	if len(q.Local) > 16 {
		q.Local = q.Local[:16]
	}
	if q.Local == nil {
		q.Local = []string{}
	}
	s.mu.Lock()
	o, ok := s.rooms[q.Room]
	if ok && o.op != q.Op {
		delete(s.rooms, q.Room)
		s.mu.Unlock()
		s.noteMatch(q.Room)
		o.ch <- map[string]any{"v": 1, "you": o.you, "peer": full, "peer_local": q.Local}
		reply(w, 200, map[string]any{"v": 1, "you": full, "peer": o.you, "peer_local": o.local})
		return
	}
	if ok || len(s.rooms) >= maxRooms {
		s.mu.Unlock()
		e := "room_busy"
		if !ok {
			e = "server_full"
		}
		reply(w, 200, errv(e))
		return
	}
	nw := &waiter{op: q.Op, you: full, local: q.Local, ch: make(chan map[string]any, 1)}
	s.rooms[q.Room] = nw
	s.mu.Unlock()
	select {
	case m := <-nw.ch:
		reply(w, 200, m)
	case <-time.After(s.ttl):
		s.mu.Lock()
		if s.rooms[q.Room] == nw {
			delete(s.rooms, q.Room)
			s.mu.Unlock()
			reply(w, 200, errv("timeout"))
			return
		}
		s.mu.Unlock()
		reply(w, 200, <-nw.ch) // matched at the last moment
	case <-r.Context().Done():
		s.mu.Lock()
		if s.rooms[q.Room] == nw {
			delete(s.rooms, q.Room)
		}
		s.mu.Unlock()
	}
}

func (s *server) handler() http.Handler {
	m := http.NewServeMux()
	m.HandleFunc("/v1/meet", s.meet)
	m.HandleFunc("/v1/ping", s.ping)
	m.HandleFunc("/v1/result", s.result)
	m.HandleFunc("/v1/stats", s.statsHandler)
	return m
}

func main() {
	a := os.Getenv("LISTEN")
	if a == "" {
		a = ":47900"
	}
	s := newServer(10 * time.Minute)
	s.statsPath = os.Getenv("STATS_FILE")
	if s.statsPath == "" {
		s.statsPath = "/data/stats.json"
	}
	s.loadStats()
	go s.sweep()
	go s.flusher()
	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGTERM, syscall.SIGINT)
	go func() { <-sig; s.flush(); os.Exit(0) }()
	srv := &http.Server{Addr: a, Handler: s.handler(), ReadHeaderTimeout: 10 * time.Second, ReadTimeout: 15 * time.Second}
	if srv.ListenAndServe() != nil {
		os.Exit(1)
	}
}
