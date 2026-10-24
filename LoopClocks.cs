using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;

namespace Echoplex;

/// <summary>
/// Animaciones en bucle que empiezan juntas (la deriva de la aurora, las luces de la pantalla completa, el sol del
/// saludo, las barritas del árbol), con tres estados: en marcha, en pausa (quietas pero a la vista: el reloj de WPF
/// sigue pidiendo fotogramas aunque no se mueva) y congeladas (sin verse: se quitan los relojes y WPF deja de dibujar;
/// se apunta cuánto llevaban y, al seguir, se crean otros que saltan a ese mismo punto antes de pintar, así que no se
/// nota). Con la ventana minimizada y la aurora solo en pausa, WPF seguía preparando unos 50 fotogramas por segundo.
/// </summary>
public sealed class LoopClocks
{
    private sealed class Entry(Animatable target, DependencyProperty property, AnimationTimeline timeline)
    {
        public Animatable Target { get; } = target;
        public DependencyProperty Property { get; } = property;
        public AnimationTimeline Timeline { get; } = timeline;
        public AnimationClock? Clock { get; set; }
    }

    private readonly List<Entry> _entries = new();
    // lo que han andado (solo corre en marcha): el punto al que vuelven tras congelarse
    private readonly Stopwatch _run = new();
    private bool _running = true;
    private bool _frozen;
    // relojes recién creados: el tiempo cuenta desde su primer tic (WPF los arranca en el siguiente fotograma, que al
    // abrir Echoplex puede llegar bastante después), no desde que se crean
    private bool _waitingTick;

    public void Add(Animatable target, DependencyProperty property, AnimationTimeline timeline)
    {
        var e = new Entry(target, property, timeline);
        _entries.Add(e);
        if (_frozen) return; // se creará al seguir
        Apply(e);
        if (!_running) e.Clock?.Controller?.Pause();
    }

    /// <summary>En marcha: desde donde se pausaron o se congelaron.</summary>
    public void Run()
    {
        if (_running && !_frozen) return;
        _running = true;
        if (_frozen)
        {
            _frozen = false;
            foreach (var e in _entries) Apply(e);
            return; // el tiempo vuelve a contar en su primer tic
        }
        foreach (var e in _entries) e.Clock?.Controller?.Resume();
        if (!_waitingTick && _entries.Count > 0) _run.Start();
    }

    /// <summary>
    /// Quietas donde están, a la vista (p. ej. mientras se arrastra el borde de la ventana, o la aurora detrás de la
    /// pantalla completa, que al cerrarse se ve con un fundido). Si estaban congeladas, vuelven a su punto, quietas.
    /// </summary>
    public void Pause()
    {
        _running = false;
        _run.Stop();
        if (_frozen)
        {
            _frozen = false;
            foreach (var e in _entries) Apply(e);
        }
        foreach (var e in _entries) e.Clock?.Controller?.Pause();
    }

    /// <summary>Sin verse: fuera los relojes, para que WPF no prepare ni un fotograma más por ellas.</summary>
    public void Freeze()
    {
        _run.Stop();
        if (_frozen) return;
        _frozen = true;
        _waitingTick = false;
        foreach (var e in _entries)
        {
            e.Clock?.Controller?.Remove();
            e.Clock = null;
        }
    }

    /// <summary>Se acabaron (otra aurora, otra página): fuera todo.</summary>
    public void Clear()
    {
        foreach (var e in _entries) e.Clock?.Controller?.Remove();
        _entries.Clear();
        _run.Reset();
        _frozen = false;
        _waitingTick = false;
    }

    private void Apply(Entry e)
    {
        var clock = e.Timeline.CreateClock();
        e.Target.ApplyAnimationClock(e.Property, clock);
        if (_run.Elapsed > TimeSpan.Zero) clock.Controller?.Seek(_run.Elapsed, TimeSeekOrigin.BeginTime);
        e.Clock = clock;
        if (_waitingTick || _run.IsRunning) return;
        // el primero de una tanda: cuando arranca de verdad, empieza a contar
        _waitingTick = true;
        EventHandler? first = null;
        first = (s, a) =>
        {
            clock.CurrentTimeInvalidated -= first;
            if (!_waitingTick || e.Clock != clock) return; // ya congelado o sustituido
            _waitingTick = false;
            if (_running && !_frozen) _run.Start();
        };
        clock.CurrentTimeInvalidated += first;
    }
}
