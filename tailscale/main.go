// echoplex-tailscale: Tailscale dentro de Echoplex, sin instalar la app de Tailscale.
//
// Es un nodo de Tailscale propio (tsnet, en espacio de usuario: sin drivers, sin VPN del sistema, sin permisos de
// administrador) que arranca y se para con Echoplex, que lo lanza como proceso hijo:
//
//   - en el PC (-mode serve): escucha en la red de Tailscale y pasa las peticiones al servidor de Echoplex en este
//     equipo (127.0.0.1), diciéndole en X-Forwarded-For quién llama, para que sepa que es «fuera de casa».
//   - en el móvil (-mode client): abre un proxy HTTP en 127.0.0.1 por el que la app llega a Echoplex del PC por Tailscale.
//
// Habla con Echoplex por líneas JSON en la salida estándar ({"type":"state",...}) y recibe órdenes por la entrada
// estándar (login, logout). Cuando la entrada se cierra (Echoplex se cerró o murió), se apaga.
package main

import (
	"bufio"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"strings"
	"sync"
	"time"

	"tailscale.com/client/local"
	"tailscale.com/ipn"
	"tailscale.com/tsnet"
)

var out = json.NewEncoder(os.Stdout)
var outMu sync.Mutex

func emit(v map[string]any) {
	outMu.Lock()
	defer outMu.Unlock()
	_ = out.Encode(v)
}

func main() {
	mode := flag.String("mode", "serve", "serve (PC) o client (móvil)")
	dir := flag.String("dir", "", "carpeta donde guarda su estado (la sesión iniciada)")
	host := flag.String("hostname", "echoplex", "nombre del equipo en Tailscale")
	port := flag.Int("port", 4040, "serve: puerto en Tailscale (el mismo que el de Echoplex)")
	target := flag.String("target", "http://127.0.0.1:4040", "serve: dónde escucha Echoplex en este equipo")
	dns := flag.String("dns", "", "client: servidores DNS del móvil, separados por comas (Android no tiene /etc/resolv.conf)")
	verbose := flag.Bool("v", false, "registro de tsnet por la salida de errores")
	flag.Parse()

	if *dns != "" {
		useDNS(strings.Split(*dns, ","))
	}
	logf := func(string, ...any) {}
	log.SetOutput(io.Discard)
	if *verbose {
		log.SetOutput(os.Stderr)
		logf = log.New(os.Stderr, "", log.LstdFlags).Printf
	}
	srv := &tsnet.Server{Dir: *dir, Hostname: *host, Logf: logf, UserLogf: logf}
	defer srv.Close()

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	if err := srv.Start(); err != nil {
		emit(map[string]any{"type": "error", "message": err.Error()})
		os.Exit(1)
	}
	lc, err := srv.LocalClient()
	if err != nil {
		emit(map[string]any{"type": "error", "message": err.Error()})
		os.Exit(1)
	}

	go watch(ctx, srv, lc)
	go commands(ctx, cancel, lc)

	switch *mode {
	case "serve":
		go serve(ctx, srv, *port, *target)
	case "client":
		go proxy(ctx, srv)
	}
	<-ctx.Done()
}

// commands: órdenes de Echoplex por la entrada estándar. Al cerrarse, se apaga todo.
func commands(ctx context.Context, cancel context.CancelFunc, lc *local.Client) {
	defer cancel()
	in := bufio.NewScanner(os.Stdin)
	for in.Scan() {
		switch strings.TrimSpace(in.Text()) {
		case "login":
			_ = lc.StartLoginInteractive(ctx)
		case "logout":
			lc2, cancel2 := context.WithTimeout(ctx, 15*time.Second)
			_ = lc.Logout(lc2)
			cancel2()
		case "quit":
			return
		}
	}
}

// watch: cuenta a Echoplex en qué estado está (hay que iniciar sesión → con qué enlace; conectado → con qué IP).
func watch(ctx context.Context, srv *tsnet.Server, lc *local.Client) {
	for ctx.Err() == nil {
		w, err := lc.WatchIPNBus(ctx, ipn.NotifyInitialState)
		if err != nil {
			time.Sleep(time.Second)
			continue
		}
		state := ""
		authURL := ""
		for {
			n, err := w.Next()
			if err != nil {
				break
			}
			changed := false
			if n.State != nil && n.State.String() != state {
				state = n.State.String()
				changed = true
				if *n.State == ipn.NeedsLogin && authURL == "" {
					go lc.StartLoginInteractive(ctx) // pide ya el enlace de inicio de sesión
				}
				if *n.State == ipn.Running {
					authURL = ""
				}
			}
			if n.BrowseToURL != nil && *n.BrowseToURL != authURL {
				authURL = *n.BrowseToURL
				changed = true
			}
			// conectado: cada aviso puede traer la IP o el nombre (report no repite si no cambia nada)
			if changed || state == "Running" {
				report(ctx, srv, lc, state, authURL)
			}
		}
		w.Close()
	}
}

var lastReport string

func report(ctx context.Context, srv *tsnet.Server, lc *local.Client, state, authURL string) {
	msg := map[string]any{"type": "state", "state": state, "authUrl": authURL}
	if state == "Running" {
		ip4, _ := srv.TailscaleIPs()
		if ip4.IsValid() {
			msg["ip"] = ip4.String()
		}
		if st, err := lc.StatusWithoutPeers(ctx); err == nil && st.Self != nil {
			msg["dnsName"] = strings.TrimSuffix(st.Self.DNSName, ".")
			if u, ok := st.User[st.Self.UserID]; ok {
				msg["user"] = u.LoginName
			}
		}
	}
	b, _ := json.Marshal(msg)
	if string(b) == lastReport {
		return
	}
	lastReport = string(b)
	emit(msg)
}

// serve (PC): lo que llega por Tailscale a :port va a Echoplex en este equipo, diciendo quién llama.
func serve(ctx context.Context, srv *tsnet.Server, port int, target string) {
	to, err := url.Parse(target)
	if err != nil {
		emit(map[string]any{"type": "error", "message": "target: " + err.Error()})
		return
	}
	ln, err := srv.Listen("tcp", fmt.Sprintf(":%d", port))
	if err != nil {
		emit(map[string]any{"type": "error", "message": "listen: " + err.Error()})
		return
	}
	rp := &httputil.ReverseProxy{
		Rewrite: func(r *httputil.ProxyRequest) {
			r.SetURL(to)
			r.Out.Host = r.In.Host
			r.SetXForwarded() // X-Forwarded-For = la IP de Tailscale del móvil
		},
		FlushInterval: -1, // el audio sale según llega
	}
	hs := &http.Server{Handler: rp}
	go func() { <-ctx.Done(); hs.Close() }()
	_ = hs.Serve(ln)
}

// proxy (móvil): un proxy HTTP en 127.0.0.1 que sale por Tailscale. La app manda por él solo lo que va a la IP de
// Tailscale del PC; lo de casa va directo.
func proxy(ctx context.Context, srv *tsnet.Server) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		emit(map[string]any{"type": "error", "message": "proxy: " + err.Error()})
		return
	}
	transport := &http.Transport{
		DialContext:         srv.Dial,
		MaxIdleConnsPerHost: 8,
		IdleConnTimeout:     90 * time.Second,
	}
	rp := &httputil.ReverseProxy{
		Rewrite:       func(r *httputil.ProxyRequest) { r.Out.URL = r.In.URL; r.Out.Host = r.In.URL.Host },
		Transport:     transport,
		FlushInterval: -1,
		// si no llega al PC, que se sepa por qué (en la respuesta y en el registro)
		ErrorHandler: func(w http.ResponseWriter, r *http.Request, err error) {
			log.Printf("proxy %s: %v", r.URL.Host, err)
			http.Error(w, "echoplex-tailscale: "+err.Error(), http.StatusBadGateway)
		},
	}
	handler := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodConnect {
			tunnel(w, r, srv)
			return
		}
		if !r.URL.IsAbs() {
			http.Error(w, "solo proxy", http.StatusBadRequest)
			return
		}
		rp.ServeHTTP(w, r)
	})
	emit(map[string]any{"type": "proxy", "port": ln.Addr().(*net.TCPAddr).Port})
	hs := &http.Server{Handler: handler}
	go func() { <-ctx.Done(); hs.Close() }()
	_ = hs.Serve(ln)
}

func tunnel(w http.ResponseWriter, r *http.Request, srv *tsnet.Server) {
	dst, err := srv.Dial(r.Context(), "tcp", r.Host)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadGateway)
		return
	}
	hj, ok := w.(http.Hijacker)
	if !ok {
		dst.Close()
		return
	}
	w.WriteHeader(http.StatusOK)
	src, _, err := hj.Hijack()
	if err != nil {
		dst.Close()
		return
	}
	go func() { io.Copy(dst, src); dst.Close() }()
	io.Copy(src, dst)
	src.Close()
}

// useDNS: en Android no hay /etc/resolv.conf; la app pasa los DNS de la red que tenga el móvil.
func useDNS(servers []string) {
	var list []string
	for _, s := range servers {
		s = strings.TrimSpace(s)
		if s == "" {
			continue
		}
		if net.ParseIP(s) != nil {
			s = net.JoinHostPort(s, "53")
		}
		list = append(list, s)
	}
	if len(list) == 0 {
		return
	}
	var i int
	var mu sync.Mutex
	net.DefaultResolver = &net.Resolver{
		PreferGo: true,
		Dial: func(ctx context.Context, network, _ string) (net.Conn, error) {
			mu.Lock()
			s := list[i%len(list)]
			i++
			mu.Unlock()
			var d net.Dialer
			return d.DialContext(ctx, network, s)
		},
	}
}
