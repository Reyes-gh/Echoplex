//go:build android

package main

// En el móvil el programa corre dentro de la app, y Android no deja a las apps ver las interfaces de red como en
// Linux (netlink): sin esto, Tailscale no arranca. androidbin (de Tailscale) da una interfaz de respaldo, usa el DNS
// del sistema (no hay /etc/resolv.conf) y los certificados de Android.
import _ "tailscale.com/feature/androidbin"
