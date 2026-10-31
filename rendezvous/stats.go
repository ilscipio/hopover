package main

import (
	"crypto/subtle"
	"encoding/json"
	"io"
	"net/http"
	"os"
	"time"
)

// Counts only. No addresses, rooms or codes are stored.
type counts struct {
	Meets      int `json:"meets"`
	DirectOK   int `json:"direct_ok"`
	DirectFail int `json:"direct_fail"`
	OKLan      int `json:"ok_lan"`
	OKInternet int `json:"ok_internet"`
}

type statsData struct {
	Total counts            `json:"total"`
	Days  map[string]counts `json:"days"`
}

type matched struct {
	at       time.Time
	reported bool
}

const (
	resultWindow = 30 * time.Minute
	keepDays     = 90
)

func today() string { return time.Now().UTC().Format("2006-01-02") }

func cutoff() string { return time.Now().UTC().AddDate(0, 0, -keepDays).Format("2006-01-02") }

// bump changes the total and today's counts.
func (s *server) bump(f func(c *counts)) {
	s.smu.Lock()
	defer s.smu.Unlock()
	f(&s.stats.Total)
	d := today()
	c := s.stats.Days[d]
	f(&c)
	s.stats.Days[d] = c
	s.dirty = true
}

func (s *server) noteMatch(room string) {
	s.mu.Lock()
	s.matches[room] = &matched{at: time.Now()}
	s.mu.Unlock()
	s.bump(func(c *counts) { c.Meets++ })
}

func (s *server) result(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		reply(w, 405, errv("bad_request"))
		return
	}
	ip, _ := addr(r)
	if !s.allow(ip) {
		reply(w, 429, errv("rate_limited"))
		return
	}
	var q struct {
		V    int    `json:"v"`
		Room string `json:"room"`
		Path string `json:"path"`
	}
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, maxBody))
	if err != nil || json.Unmarshal(body, &q) != nil || q.V != 1 || !roomRe.MatchString(q.Room) ||
		(q.Path != "lan" && q.Path != "internet" && q.Path != "none") {
		reply(w, 400, errv("bad_request"))
		return
	}
	s.mu.Lock()
	m := s.matches[q.Room]
	ok := m != nil && !m.reported && time.Since(m.at) <= resultWindow
	if ok {
		m.reported = true
	}
	s.mu.Unlock()
	if !ok {
		reply(w, 200, map[string]any{"v": 1, "counted": false})
		return
	}
	s.bump(func(c *counts) {
		switch q.Path {
		case "lan":
			c.DirectOK++
			c.OKLan++
		case "internet":
			c.DirectOK++
			c.OKInternet++
		default:
			c.DirectFail++
		}
	})
	reply(w, 200, map[string]any{"v": 1, "counted": true})
}

func (s *server) statsHandler(w http.ResponseWriter, r *http.Request) {
	key := os.Getenv("STATS_KEY")
	if key == "" {
		http.NotFound(w, r)
		return
	}
	if subtle.ConstantTimeCompare([]byte(r.Header.Get("Authorization")), []byte("Bearer "+key)) != 1 {
		http.NotFound(w, r)
		return
	}
	reply(w, 200, s.snapshot())
}

// snapshot copies the stats; days older than keepDays are left out.
func (s *server) snapshot() statsData {
	s.smu.Lock()
	defer s.smu.Unlock()
	out := statsData{Total: s.stats.Total, Days: map[string]counts{}}
	cut := cutoff()
	for d, c := range s.stats.Days {
		if d > cut {
			out.Days[d] = c
		}
	}
	return out
}

func (s *server) loadStats() {
	if s.statsPath == "" {
		return
	}
	b, err := os.ReadFile(s.statsPath)
	if err != nil {
		return
	}
	var d statsData
	if json.Unmarshal(b, &d) != nil {
		return
	}
	if d.Days == nil {
		d.Days = map[string]counts{}
	}
	s.stats = d
}

// flush writes the file atomically when something changed.
func (s *server) flush() {
	if s.statsPath == "" {
		return
	}
	s.smu.Lock()
	if !s.dirty {
		s.smu.Unlock()
		return
	}
	cut := cutoff()
	for d := range s.stats.Days {
		if d <= cut {
			delete(s.stats.Days, d)
		}
	}
	b, _ := json.Marshal(s.stats)
	s.dirty = false
	s.smu.Unlock()
	tmp := s.statsPath + ".tmp"
	if os.WriteFile(tmp, b, 0o644) != nil || os.Rename(tmp, s.statsPath) != nil {
		s.smu.Lock()
		s.dirty = true
		s.smu.Unlock()
	}
}

func (s *server) flusher() {
	for range time.Tick(10 * time.Second) {
		s.flush()
	}
}
