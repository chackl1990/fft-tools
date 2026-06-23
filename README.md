# fft_tools r106 README-Dokumentation

Diese README beschreibt die aktuell vorhandenen Modi und alle über die Kommandozeile erreichbaren Parameter.

Wichtig: Der Ausgabepfad muss direkt nach dem Modus stehen. Optionen kommen danach.

Richtig:

```text
fft_tools.exe input.wav --upmix output_7_1.wav --wide-gain 100 -o
```

Falsch:

```text
fft_tools.exe input.wav --upmix --wide-gain 100 output_7_1.wav -o
```

Der Dezimaltrenner ist intern Punkt. Komma wird beim Einlesen der Parameter ebenfalls akzeptiert.

---

# Allgemeine Struktur

```text
fft_tools.exe <input> <modus> <output> [parameter] [-o] [-t <threads>]
```

Es darf pro Aufruf immer nur ein Hauptmodus aktiv sein.

Vorhandene Hauptmodi:

```text
--enhance
--dereverb
--upmix
--upmix-spectrum
```

Globale Parameter:

```text
-o
-t <count>
```

## WAV-Formatunterstützung

WAV-Eingabe unterstützt jetzt:

```text
PCM Integer: 8/16/24/32 Bit
IEEE Float: 32 Bit
IEEE Float: 64 Bit / Float64 / Double
```

WAV-Ausgabe verhält sich format-erhaltend:

```text
Integer-Eingang -> Integer-Ausgang mit gleicher Bittiefe
Float32-Eingang -> Float32-Ausgang
Float64-Eingang -> Float64-Ausgang
```

Das gilt auch für temporäre WAV-Dateien im Streaming-/Enhance-/Resample-/Normalize-Pfad. Float64 wird nur für WAV unterstützt; FLAC/WavPack-Float64 ist hier nicht gemeint.

---

## -o

Default: aus.

Überschreibt vorhandene Ausgabedateien ohne Rückfrage.

Ohne `-o` fragt das Tool nach, wenn die Ausgabedatei bereits existiert.

Sinnvoll:

```text
-o
```

für Batch-Tests, automatische Skripte und schnelle Vergleichsläufe.

## -t <count>

Default: `0`, also interne Standard-Threadanzahl.

Erlaubter Bereich im Parser: `1..32`, wenn gesetzt.

Wirkt auf interne Worker-Tasks, vor allem bei Resampling/Normalisierung und rechenintensiven FFT-Pfaden.

Höher:

```text
-t 8
-t 16
```

kann schneller sein, kann aber auf kleinen CPUs oder bei viel I/O auch unnötig Last erzeugen.

Niedriger:

```text
-t 1
-t 2
```

ist sinnvoll für Debugging, schwache Rechner oder wenn nebenbei andere Prozesse laufen sollen.

---

# --enhance

## Zweck

`--enhance` ist der Harmonic-/Hochfrequenz-Enhancer. Er arbeitet per FFT, sucht einen geeigneten Enhance-Bereich und erzeugt zusätzliche Oktav-Harmonics. Das Originalspektrum bleibt erhalten; die Harmonics werden additiv ergänzt.

Aufruf:

```text
fft_tools.exe input.wav --enhance output.wav
```

Beispiel mit Zielrate und Normalisierung:

```text
fft_tools.exe input.wav --enhance output.wav --rate 96000 -n -1 -o
```

Parameter:

```text
--rate <0|1|2|4|8|hz>
-r <0|1|2|4|8|hz>
-n <db>
-o
-t <count>
```

## --rate / -r <0|1|2|4|8|hz>

Default: `0`.

Legt die Ziel-Samplerate für den Enhance-Output fest.

Bedeutung:

```text
0 oder 1 = Eingangssamplerate beibehalten
2        = Eingangssamplerate * 2
4        = Eingangssamplerate * 4
8        = Eingangssamplerate * 8
>8       = absoluter Hz-Wert, z. B. 96000 oder 192000
```

Beispiele:

```text
--rate 0
--rate 2
--rate 96000
-r 192000
```

Höher:

Mehr Zielbandbreite und mehr Platz für erzeugte Harmonics. Sinnvoll, wenn ein 44,1/48-kHz-Track auf 88,2/96/176,4/192 kHz erweitert werden soll.

Tiefer beziehungsweise `0`:

Bleibt näher am Originalformat. Weniger Rechenzeit und kleinere Dateien. Wenn die Zielrate nicht höher ist, kann der Enhance-Effekt begrenzter wirken, weil oberhalb der ursprünglichen Nyquist-Grenze kein echter Zielbereich vorhanden ist.

Sinnvolle Werte:

```text
0       = keine Samplerate-Änderung
2       = einfacher Test mit doppelter Rate
96000   = typischer 48-kHz-kompatibler Zielwert
192000  = sehr hohe Zielrate, mehr CPU/Dateigröße
```

## -n <db>

Default: aus.

Aktiviert eine Peak-Normalisierung nach Enhance/Resampling. Der Wert ist der Ziel-Peak in dBFS.

Beispiele:

```text
-n -1
-n -0.5
-n -3
```

Höher, also näher an `0`:

Der Output wird lauter. `-0.1` oder `0` nutzt den Headroom fast vollständig aus. Das kann für Tests praktisch sein, lässt aber wenig Sicherheit für nachfolgende Verarbeitung.

Niedriger:

Mehr Headroom. `-1` bis `-3` ist für weitere Verarbeitung meist sicherer.

Sinnvolle Werte:

```text
-3 dB  = sehr sicher
-1 dB  = guter Standard für Dateien
-0.5 dB = laut, aber noch etwas Reserve
0 dB   = maximaler Peak, riskanter für weitere Verarbeitung
```

## Interne Enhance-Defaults

Diese Werte sind derzeit nicht als CLI-Parameter herausgeführt, aber wichtig für das Verständnis:

```text
44,1 / 48 kHz -> FFT 8192
96 kHz        -> FFT 16384
192 kHz       -> FFT 32768
Hop           -> ca. 10 % Fensterlänge, auf ungeraden Integer korrigiert
Harmonic-Fade -> 1/48 Oktave je Seite
Enhance-Bin   -> wird automatisch gesucht und um 1/48 Oktave abgesenkt
```

---

# --dereverb

## Zweck

`--dereverb` ist der reine 6-Kanal-Testmodus für zeitbasierte Halltrennung. Er erzeugt keinen 7.1-Upmix und kein künstliches Reverb. Er trennt das Stereo-Signal in Dry, Nah-Hall und Fern-Hall.

Aufruf:

```text
fft_tools.exe input.wav --dereverb output_6ch.wav -o
```

Ausgabe-Kanalreihenfolge:

```text
1 = L dry
2 = R dry
3 = L near reverb
4 = R near reverb
5 = L far reverb
6 = R far reverb
```

Rechnerischer Downmix:

```text
L original = L dry + L near + L far
R original = R dry + R near + R far
```

Das Ziel ist: nichts dazudichten und nichts weglassen.

Aktueller r102-Dry/Reverb-Split:

```text
early/late Reverb werden per zeitlicher Maske erkannt
die Reverb-Masken werden zeitlich geglättet
die Reverb-Masken werden danach weich über Nachbarbins geglättet
dry bleibt trotzdem exakt: original - earlyReverb - lateReverb
```

Die Nachbarbin-Glättung wird nur auf den Reverb-Anteil angewendet. Es gibt keinen zusätzlichen Dry-Filter und keine harten Frequenzstufen.

Parameter:

```text
--dereverb-lowcut <hz>
--dereverb-early-start <ms>
--dereverb-early-full <ms>
--dereverb-late-start <ms>
--dereverb-max-ms <ms>
--dereverb-strength <pct>
--dereverb-tonal-protect <0..1|pct>
-o
-t <count>
```

Interne FFT-Basis:

```text
44,1 / 48 kHz -> FFT 8192
96 kHz        -> FFT 16384
192 kHz       -> FFT 32768
Overlap       -> 16x
```

Bei 48 kHz ergibt 8192/16x einen Hop von 512 Samples, also ca. 10,7 ms. Das ist die zeitliche Rasterung der Hall-Erkennung. Die gleiche 16x-Overlap-Grundeinstellung wird auch für die spektralen Upmix-FFTs verwendet.

## --dereverb-lowcut <hz>

Default: `100`.

Legt die Mitte der weichen Lowcut-Kurve für die Hall-Extraktion fest.

Es ist keine harte Frequenzstufe. Die Kurve blendet kontinuierlich ein:

```text
lowcut * 0.75 -> praktisch keine Hall-Extraktion
lowcut        -> Übergangsmitte
lowcut * 1.25 -> volle Hall-Erkennung
```

Bei Default `100 Hz`:

```text
75 Hz  -> 0 %
100 Hz -> Übergang
125 Hz -> 100 %
```

Höher:

Weniger Bass und Low-Mid wird als Hall extrahiert. Das schützt Bass, Kick, tiefe Synths und Grundton-Ausklänge. Der Effekt wird sauberer, aber eventuell weniger voll.

Tiefer:

Mehr tiefe Hallanteile werden extrahiert. Das kann räumlicher wirken, aber auch Bass, Sustain oder Raumresonanzen stärker aus dem Dry-Signal ziehen.

Sinnvolle Werte:

```text
80 Hz   = lässt mehr unteren Raumanteil zu
100 Hz  = aktueller Default / guter Startwert
120 Hz  = sicherer gegen Bassfehler
150 Hz  = sehr konservativ im Bassbereich
```

## --dereverb-early-start <ms>

Default: `40`.

Ab dieser Zeit nach einem erkannten Attack darf Nah-Hall beginnen.

Höher:

Der Algorithmus wartet länger, bevor er etwas als Hall betrachtet. Das schützt Transienten und kurze Ausklänge, macht die Trennung aber konservativer.

Tiefer:

Nah-Hall wird früher erkannt. Dadurch wird mehr Raumanteil extrahiert, aber die Gefahr steigt, dass frühe Instrumentenanteile oder Anschlagsausklang in den Hall-Kanälen landen.

Sinnvolle Werte:

```text
20 ms = aggressiv
30 ms = hörbar mehr Raum, noch brauchbar zum Testen
40 ms = Default
60 ms = vorsichtig
80 ms = sehr konservativ
```

## --dereverb-early-full <ms>

Default: `80`.

Ab dieser Zeit erreicht der Nah-Hall seine volle Zeitgewichtung. Zwischen `early-start` und `early-full` wird weich eingeblendet.

Muss intern mindestens etwas größer als `early-start` sein. Wenn ein zu kleiner Wert gesetzt wird, wird er im Code auf mindestens `early-start + 1 ms` angehoben.

Höher:

Der Nah-Hall baut sich langsamer auf. Das klingt vorsichtiger und schützt direkte Ausklänge.

Tiefer:

Der Nah-Hall erreicht schneller volle Stärke. Dadurch wird der Effekt deutlicher, aber aggressive Werte können Attack-/Sustain-Reste in den Near-Kanälen hörbarer machen.

Sinnvolle Werte:

```text
50 ms  = deutlich aggressiver
70 ms  = guter Testwert für mehr Effekt
80 ms  = Default
120 ms = vorsichtiger
```

## --dereverb-late-start <ms>

Default: `160`.

Ab dieser Zeit beginnt der Anteil zunehmend vom Nah-Hall in Richtung Fern-Hall zu wandern.

Muss intern größer als `early-full` sein. Wenn ein zu kleiner Wert gesetzt wird, wird er auf mindestens `early-full + 1 ms` angehoben.

Höher:

Mehr Hall bleibt in den Near-Kanälen. Back/Far wird später und dezenter.

Tiefer:

Mehr Hall landet früher im Fern-Hall. Im späteren `--upmix` bedeutet das: mehr Zusatz in den Back-Kanälen.

Sinnvolle Werte:

```text
100 ms = aggressiv, mehr Far/Back-Anteil
140 ms = etwas stärker als Default
160 ms = Default
220 ms = vorsichtig
300 ms = sehr später Far-Hall
```

## --dereverb-max-ms <ms>

Default: `600`.

Ab dieser Zeit ist der Übergang zum Fern-Hall vollständig. Zwischen `late-start` und `max-ms` wird weich von Near zu Far überblendet.

Muss intern größer als `late-start` sein. Wenn ein zu kleiner Wert gesetzt wird, wird er auf mindestens `late-start + 1 ms` angehoben.

Höher:

Der Fern-Hall baut sich langsamer auf. Lange Hallfahnen bleiben differenzierter und weniger schnell hinten.

Tiefer:

Fern-Hall erreicht schneller volle Stärke. Dadurch werden Back/Far-Kanäle deutlicher, aber bei zu kleinen Werten kann es unnatürlich wirken.

Sinnvolle Werte:

```text
350 ms = deutlich mehr Far-Hall
450 ms = stärker, aber noch kontrolliert
600 ms = Default
800 ms = vorsichtig
1000 ms = sehr langsame Far-Zuordnung
```

## --dereverb-strength <pct>

Default: `100`.

Skaliert die Hall-Extraktion insgesamt.

Wichtig: Es gibt keine feste maximale Hallmaske mehr. Die Begrenzung erfolgt über den Direct-Floor-Schutz, damit der Dry-Bin nicht sinnlos leergezogen wird.

Höher:

Mehr erkannter Hall wird aus Dry herausgezogen und in Near/Far verschoben. Der Effekt wird klarer.

Tiefer:

Weniger Halltrennung, mehr bleibt im Dry-Signal.

Sinnvolle Werte:

```text
0    = Halltrennung praktisch aus
50   = sehr vorsichtig
100  = Default
125  = etwas stärker
150  = deutlich stärkerer Testwert
200  = aggressiv, Risiko für hörbare Artefakte
```

## --dereverb-tonal-protect <0..1|pct>

Default: `0.00` / aus.

Optionale zusätzliche Schutzgewichtung für stabile, tonale und lokal peakartige Anteile vor der Hall-Extraktion. Standardmäßig ist diese Zusatzgewichtung abgeschaltet, weil sie in vielen Tests zu viel echten Hall blockiert hat. Andere Schutzmechanismen wie Re-Attack-Erkennung, Direct-Floor und Maskenglättung bleiben trotzdem aktiv.

Der Parameter akzeptiert Ratio oder Prozent:

```text
0.90
90
```

beides bedeutet 90 % zusätzliche Schutzgewichtung. `0` oder `0.00` bedeutet abgeschaltet.

Höher:

Stabile Töne bleiben stärker im Dry-Signal. Weniger Risiko für kaputte Instrumentenausklänge, aber auch weniger Hall-Extraktion.

Tiefer:

Mehr Tail wird als Hall extrahiert. Der Effekt wird deutlicher, aber es steigt das Risiko, dass Sustain von Stimme, Gitarre, Piano, Synth oder Becken in Near/Far wandert.

Sinnvolle Werte:

```text
1.00 = maximaler Schutz
0.90 = Default / sicher
0.75 = mehr Hall, noch brauchbarer Testwert
0.60 = deutlich aggressiver
0.30 = riskant
0.00 = kein tonal protection, nur für Extremtests
```

## Empfohlene Dereverb-Testaufrufe

Default-Test:

```text
fft_tools.exe input.wav --dereverb out_6ch.wav -o
```

Konservativer bei problematischen Instrumenten-Ausklängen:

```text
fft_tools.exe input.wav --dereverb out_6ch.wav --dereverb-tonal-protect 0.75 -o
```

Mehr früher Raum:

```text
fft_tools.exe input.wav --dereverb out_6ch.wav --dereverb-early-start 30 --dereverb-early-full 70 -o
```

Mehr Fern-Hall:

```text
fft_tools.exe input.wav --dereverb out_6ch.wav --dereverb-late-start 120 --dereverb-max-ms 450 -o
```

---

# --upmix

## Zweck

`--upmix` ist der aktuelle 7.1-Hauptmodus. Er nutzt zuerst die zeitbasierte Dereverb-Zerlegung. Danach wird Dry plus fest 50 % des Nah-Halls mit dem spektralen LCR-Upmix auf L/C/R gerechnet. Der vollständige Near-Hall geht zusätzlich in die Side-Kanäle, der vollständige Far-Hall in die Back-Kanäle.

Aufruf:

```text
fft_tools.exe input.wav --upmix output_7_1.wav -o
```

Signalfluss:

```text
Stereo Input
  -> Dereverb: dry / near / far
  -> dry + near*0.50: spektraler LCR-Upmix nach L/C/R
  -> near: vollständig auf SL/SR
  -> far: vollständig auf BL/BR
  -> finale Center-Referenz-Korrektur
```

AVR-/WAV-Kanalreihenfolge:

```text
1 = FL
2 = FR
3 = C
4 = LFE
5 = SL
6 = SR
7 = BL
8 = BR
```

Internes Mapping:

```text
frontL/R = dryL/R + nearL/R * 0.50

FL  = front L-Anteil ohne Center
FR  = front R-Anteil ohne Center
C   = front Center
LFE = 0
SL  = near L
SR  = near R
BL  = far L
BR  = far R
```

Festes Verhalten im aktuellen `--upmix`:

```text
50 % des Nah-Halls bleiben zusätzlich in der Frontbasis L/C/R.
100 % des Nah-Halls gehen weiterhin auf SL/SR.
0 % des Fern-Halls bleiben in der Frontbasis; Far geht nur auf BL/BR.
```

Danach kommt final die Center-Referenz-Korrektur:

```text
C bleibt unverändert
alle Nicht-Center-Kanäle *= 0.7071067811865476
```

Das entspricht relativ betrachtet einem Center-Vorteil von ca. +3 dB, ohne den Center selbst über 0 dB anzuheben.

Center-Phasenregel in der aktuellen Hauptrevision:

```text
CenterPhaseFullDelayMs = 5.0 ms
CenterPhaseZeroDelayMs = 10.0 ms

Die erlaubten Phasenwinkel werden pro Frequenz aus dieser Laufzeitdifferenz berechnet:
phaseDegrees = frequencyHz * delayMs * 0.36
```

Ein separates `CenterPhaseMaxZeroDegrees` gibt es nicht mehr. Die gemessene Phasendifferenz kommt aus `Math.Acos(...)` und liegt dadurch ohnehin nur im Bereich 0° bis 180°. Die Center-Maske wird frequenzseitig über Nachbarbins geglättet, aber nicht zeitlich geglättet. Dadurch bleiben echte Center-Anteile schneller erreichbar, während schmale Ein-Bin-Minima weniger flattern. Die spätere -3-dB-Absenkung der Nicht-Center-Kanäle wird in der internen Gewichtung berücksichtigt.

Ab r128 sind `--upmix`, `--upmix-spectrum`, `--dereverb` und `--enhance` in eigene FFTTools-Dateien aufgeteilt; `--upmix-spectrum` verwendet keinen alten FFTToolsProcessor-Core mehr.

Parameter:

```text
-a <percent>
--center-gain <pct>
--wide-gain <pct>
--wide-exp <value>
--wide-lowcut <hz>
--wide-phase <value>
--wide-smooth <ms>
--pan-sharpness <value>
--clcr-pos <value>
--dereverb-lowcut <hz>
--dereverb-early-start <ms>
--dereverb-early-full <ms>
--dereverb-late-start <ms>
--dereverb-max-ms <ms>
--dereverb-strength <pct>
--dereverb-tonal-protect <0..1|pct>
-o
-t <count>
```

Die Upmix-Parameter wirken auf den Dry-/Front-Upmix. Die Dereverb-Parameter steuern die Trennung in Dry/Near/Far, bevor der 7.1-Upmix entsteht. Der Nah-Hall-Front-Erhalt ist aktuell fest auf 50 % gesetzt und hat keinen eigenen CLI-Parameter.

Hinweis ab r107: Die spektralen Front-Panning-Gewichte für `L/CL/C/CR/R` werden leicht geglättet. Dabei wird nur die Zuordnungsmaske geglättet, nicht das Audiosignal selbst. Die Gewichtung wird erst über Nachbarbins geglättet und danach zeitlich geglättet, damit `L`, `CL`, `C`, `CR` und `R` weniger metallisch klingen, ohne den spektralen Upmix grundsätzlich zu verlieren.

## -a <percent>

Default: `100`.

Globaler Amplitudenfaktor für den Upmix-Output.

Beispiele:

```text
-a 100
-a 90
-a 70
```

Höher:

Output wird lauter. Werte über 100 erhöhen Clipping-Risiko.

Tiefer:

Mehr Headroom. Sinnvoll, wenn `--wide-gain`, `--dereverb-strength` oder die Center-/Hall-Mischung sehr kräftig eingestellt sind.

Sinnvolle Werte:

```text
100 = Default
90  = etwas Headroom
70  = viel Headroom für aggressive Tests
120 = nur mit Vorsicht
```

## --center-gain <pct>

Default: `100`.

Skaliert die kohärenten, fokussierten Dry-Anteile des LCR7-Upmixes: focused L, CL, C, CR und focused R. Das ist nicht dasselbe wie die finale Center-Referenz-Korrektur. Die finale Korrektur kommt danach immer noch separat: Center bleibt, Nicht-Center -3 dB.

Intern wird der Wert als Faktor `pct / 100` verwendet und auf `0..200 %` begrenzt.

Höher:

Mehr fokussierte Front-/Center-Energie. Stimmen und mittige Quellen werden präsenter, aber zu hohe Werte können den komplementären Rest in L/R stärker verändern und Center/Front überbetonen.

Tiefer:

Weniger fokussierte Center-/Front-Zuordnung. Mehr bleibt in den residualen L/R-Anteilen. Das kann breiter und vorsichtiger klingen, aber der Center kann weniger stabil wirken.

Sinnvolle Werte:

```text
80   = vorsichtig
100  = Default
110  = leichte Verstärkung
120  = deutlichere Front/Center-Fokussierung
150+ = aggressiv
200  = technisches Maximum
```

## --wide-gain <pct>

Default: `35`.

Skaliert die spektral extrapolierten Wide-Anteile WL/WR des internen Dry-/Front-Upmixes. Im neuen `--upmix` werden diese Wide-Anteile nicht mehr direkt auf Side/Back ausgegeben, sondern in die L/R-Frontbasis zurückgefaltet. In `--upmix-spectrum` landen WL/WR weiterhin auf Back L/R.

Intern wird der Wert als Faktor `pct / 100` verwendet und auf `0..400 %` begrenzt.

Höher:

Im neuen `--upmix`: mehr breiter Front-/L/R-Anteil aus der spektralen Analyse. In `--upmix-spectrum`: mehr Dry-Wide/Back-Anteil.

Tiefer:

Im neuen `--upmix`: weniger spektrisch verbreiterte Frontbasis. In `--upmix-spectrum`: weniger Dry-Wide/Back-Anteil.

Sinnvolle Werte:

```text
0    = spektrale Dry-Wide-Extraktion aus
20   = sehr dezent
35   = Default
60   = deutlich
100  = stark, Testwert
150+ = sehr aggressiv
400  = technisches Maximum
```

## --wide-exp <value>

Default: `2.5`.

Exponent für die Wide-Maske. Er bestimmt, wie streng ein Bin als Wide/Diffus-Anteil gewertet wird.

Intern gilt mindestens `0.1`.

Niedriger:

Mehr Bins werden als Wide zugelassen. Der Effekt wird breiter und stärker. Werte wie `0.5` oder `1.0` sind deutlich offener als der Default.

Höher:

Nur noch sehr starke Wide-Kandidaten kommen durch. Der Effekt wird selektiver und oft sauberer, aber schwächer.

Sinnvolle Werte:

```text
0.5 = sehr offen / viel Wide
1.0 = offen
2.5 = Default
4.0 = selektiv
6.0 = sehr selektiv
```

## --wide-lowcut <hz>

Default: `250`.

Bestimmt, ab welcher Frequenz die spektrale Dry-Wide-Extraktion sinnvoll ansteigt. Die Kurve ist weich und nicht hart gestuft.

Höher:

Weniger Low-Mid/Bass in den Wide-/Back-Anteilen. Das klingt meist kontrollierter und schützt Bass/Grundton.

Tiefer:

Mehr untere Frequenzen können in die Wide-/Back-Anteile gelangen. Das kann voller wirken, aber schneller schwammig werden.

Sinnvolle Werte:

```text
80   = sehr viel Low-Anteil, riskanter
100  = breiter, aber noch kontrollierbar
150  = guter tiefer Testwert
250  = Default
400  = vorsichtiger
600+ = nur obere Mitten/Höhen stark im Wide
```

## --wide-phase <value>

Default: `1.0`.

Gewichtet, wie stark die L/R-Phasenlage in die Wide-Erkennung eingeht.

Intern begrenzt auf `0..2`.

Niedriger:

Die Wide-Maske verlässt sich weniger auf Phasen-Gegensätzlichkeit. Bei `0` wird die Phase praktisch nicht gewichtet. Dadurch kann mehr Material als Wide durchkommen.

Höher:

Nur phasenmäßig deutlich breite/diffuse Anteile werden stärker zugelassen. Das kann sauberer sein, aber den Effekt reduzieren.

Sinnvolle Werte:

```text
0.0 = Phase ignorieren
0.5 = weniger streng
1.0 = Default
1.5 = strenger
2.0 = technisches Maximum / sehr streng
```

## --wide-smooth <ms>

Default: `80`.

Wichtig: Dieser Parameter wird aktuell vom Parser akzeptiert, hat im aktuellen Codepfad aber keine hörbare Wirkung. Er ist aus Kompatibilitätsgründen vorhanden.

Höher/tiefer:

Derzeit keine praktische Auswirkung, solange die interne Smooth-Logik nicht wieder aktiv genutzt wird.

Sinnvoll:

```text
--wide-smooth 80
```

als Platzhalter/Kompatibilitätswert. Für Klangtests derzeit nicht relevant.

## --pan-sharpness <value>

Default: `1.0`.

Schärft oder verbreitert die Zuordnung der fokussierten Dry-Anteile auf die fünf Frontpositionen:

```text
L, CL, C, CR, R
```

Intern gilt mindestens `0.1`.

Bei `1.0` ist die Interpolation linear.

Höher:

Quellen werden stärker auf den nächstliegenden Anker gezogen. Das macht einzelne Positionen klarer, kann aber weniger natürlich und weniger breit wirken.

Tiefer:

Quellen werden weicher zwischen benachbarten Ankern verteilt. Das klingt breiter, aber weniger präzise.

Sinnvolle Werte:

```text
0.5 = sehr weich
0.8 = leicht weich
1.0 = Default / linear
1.5 = etwas fokussierter
2.0 = deutlich fokussierter
3.0+ = oft zu hart
```

## --clcr-pos <value>

Default: `0.5`.

Setzt die Position der CL/CR-Anker innerhalb des Frontpanoramas.

Die internen Anker sind:

```text
L  = -1.0
CL = -clcr-pos
C  =  0.0
CR = +clcr-pos
R  = +1.0
```

Intern begrenzt auf `0.05..0.95`.

Kleiner:

CL/CR rücken näher zum Center. Der Bereich zwischen CL/CR und L/R wird größer. Beispiel: Bei `0.333` liegt eine Position `+0.666` genau zwischen CR und R, also bei `--pan-sharpness 1.0` etwa 50 % CR und 50 % R.

Größer:

CL/CR rücken näher an L/R. Der Centerbereich wird breiter und die Zwischenkanäle liegen weiter außen.

Sinnvolle Werte:

```text
0.333 = CL/CR bei etwa Drittelposition
0.5   = Default / mittige Zwischenposition
0.6   = weiter außen
0.75  = stark außen
0.95  = technisches Maximum, fast bei L/R
```

## Dereverb-Parameter in --upmix

Alle Dereverb-Parameter aus dem Abschnitt `--dereverb` gelten auch für `--upmix`. Der Unterschied ist nur die Verwendung:

```text
--dereverb:
    schreibt Dry/Near/Far als 6-Kanal-Testdatei

--upmix:
    nutzt Dry plus fest 50 % Near-Anteil als Frontbasis für L/C/R,
    Near vollständig auf SL/SR,
    Far vollständig auf BL/BR
```

Beispiel mit stärkerem Hall im Upmix:

```text
fft_tools.exe input.wav --upmix out_7_1.wav --dereverb-strength 150 --dereverb-tonal-protect 0.75 -o
```

Beispiel mit stärkerem spektralem Wide-Anteil:

```text
fft_tools.exe input.wav --upmix out_7_1.wav --wide-gain 100 --wide-exp 0.5 --wide-lowcut 100 --clcr-pos 0.333 -o
```

---

# --upmix-spectrum

## Zweck

`--upmix-spectrum` ist der bisherige reine spektrale 7.1-Upmix. Er nutzt keine vorherige Dereverb-Zerlegung. Er entspricht dem alten `--upmix` vor der Dereverb-Integration.

Aufruf:

```text
fft_tools.exe input.wav --upmix-spectrum output_7_1.wav -o
```

AVR-/WAV-Kanalreihenfolge:

```text
1 = FL
2 = FR
3 = C
4 = LFE
5 = SL
6 = SR
7 = BL
8 = BR
```

Internes Mapping:

```text
FL  = CL
FR  = CR
C   = C
LFE = 0
SL  = L
SR  = R
BL  = WL
BR  = WR
```

Danach kommt final ebenfalls die Center-Referenz-Korrektur:

```text
C bleibt unverändert
alle Nicht-Center-Kanäle *= 0.7071067811865476
```

Auch `--upmix-spectrum` nutzt die feste Center-Phasenregel der aktuellen Hauptrevision:

```text
CenterPhaseFullDelayMs = 5.0 ms
CenterPhaseZeroDelayMs = 10.0 ms

Die erlaubten Phasenwinkel werden pro Frequenz aus dieser Laufzeitdifferenz berechnet:
phaseDegrees = frequencyHz * delayMs * 0.36
```

Der 7-Punkt-Spektrum-Upmix bleibt erhalten; nur die Center-Entstehung wird vorher phasengeprüft.

Parameter:

```text
-a <percent>
--center-gain <pct>
--wide-gain <pct>
--wide-exp <value>
--wide-lowcut <hz>
--wide-phase <value>
--wide-smooth <ms>
--pan-sharpness <value>
--clcr-pos <value>
-o
-t <count>
```

Die Bedeutung dieser Parameter ist identisch zum Upmix-Parameterblock im Abschnitt `--upmix`. Der Unterschied: `--upmix-spectrum` verwendet keine Dereverb-Parameter und mischt keinen Near/Far-Hall auf Side/Back.

Auch `--upmix-spectrum` nutzt ab r107 die leichte Front-Panning-Glättung über Nachbarbins und Zeit, weil dieser Modus denselben spektralen LCR7-Frontsplit verwendet.

Beispiele:

```text
fft_tools.exe input.wav --upmix-spectrum out_7_1.wav -o
```

Mehr Wide/Back:

```text
fft_tools.exe input.wav --upmix-spectrum out_7_1.wav --wide-gain 100 --wide-exp 1.0 -o
```

CL/CR näher zur Mitte:

```text
fft_tools.exe input.wav --upmix-spectrum out_7_1.wav --clcr-pos 0.333 -o
```

---

# Schnellübersicht der Defaults

```text
Allgemein:
-o                         aus
-t                         0 / intern

--enhance:
--rate / -r                0
-n                         aus

--dereverb:
--dereverb-lowcut          100 Hz
--dereverb-early-start     40 ms
--dereverb-early-full      80 ms
--dereverb-late-start      160 ms
--dereverb-max-ms          600 ms
--dereverb-strength        100 %
--dereverb-tonal-protect   0.00 / aus
Dereverb FFT Overlap     16x
Reverb-Maskenglättung    zeitlich + Nachbarbins, intern

--upmix / --upmix-spectrum:
-a                         100 %
--center-gain              100 %
--wide-gain                35 %
--wide-exp                 2.5
--wide-lowcut              250 Hz
--wide-phase               1.0
--wide-smooth              80 ms, aktuell ohne Wirkung
--pan-sharpness            1.0
--clcr-pos                 0.5

finale 7.1-Center-Korrektur:
C                          0 dB
alle Nicht-Center-Kanäle   -3 dB / Faktor 0.7071067811865476
```

---

# Typische Arbeitsbefehle

Aktueller Haupt-Upmix:

```text
fft_tools.exe input.wav --upmix output_7_1.wav -o
```

Alter spektraler Upmix:

```text
fft_tools.exe input.wav --upmix-spectrum output_7_1_spectrum.wav -o
```

Reiner Dereverb-Test:

```text
fft_tools.exe input.wav --dereverb output_6ch.wav -o
```

Enhance auf 96 kHz mit Normalisierung:

```text
fft_tools.exe input.wav --enhance output_96k.wav --rate 96000 -n -1 -o
```

---

# r126: Clean-Upmix-Core und Source-Vergleich

Diese Revision trennt den aktiven `--upmix`-Dry-Center-Pfad weiter von der historischen Codebasis:

```text
--upmix:
    eigener FFTToolsUpmix-Core
    Center = komplexes Mid-Signal * eigene Center-Maske
    L/R-Residual = L/R - Center
    kein alter Alpha-Pfad im aktiven Dry-LCR-Upmix
```

Die Center-Maske bleibt die aktuelle chackl-Logik:

```text
CenterPhaseFullDelayMs = 5.0 ms
CenterPhaseZeroDelayMs = 10.0 ms
Nachbar-Bin-Glättung der Center-Maske
```

Zusätzlich wurden zwei kleine Hilfsbereiche unabhängig neu geschrieben:

```text
Library/WorkerPool.cs
Library/SampleCodec.cs
```

Hinweis zur Code-Herkunft:

```text
--upmix-spectrum enthält weiterhin historische spektrale Strukturanteile und ist noch nicht vollständig unabhängig neu geschrieben.
```

Weitere technische Ähnlichkeiten sollten anhand des aktuellen Vergleichsberichts geprüft werden.

# r128: Mode-Dateien und weitere Entkopplung

Diese Revision trennt die aktiven Verarbeitungsbereiche stärker in eigene Dateien:

```text
Library/FFTToolsUpmix.cs
    neuer Name für den bisherigen phase-aware Center-Code
    enthält die Center-Phasen-ms-Konstanten und den rekonstruktiven Dry-Center-Extractor

Library/FFTToolsUpmixSpectrum.cs
    enthält den spektralen 7-Punkt-Upmix-Pfad inklusive eigenem Core

Library/FFTToolsDereverb.cs
    enthält DereverbOptions und den aktiven Dereverb-Prozessor

Library/FFTToolsEnhance.cs
    enthält EnhanceOptions und den aktiven Enhance-Prozessor

Library/FFTToolsTypes.cs
    enthält gemeinsame Delegates und LCRWideOptions
```

Die Center-Phasen-Konstanten stehen jetzt hier:

```csharp
// Library/FFTToolsUpmix.cs
public const double CenterPhaseFullDelayMs = 5.0;
public const double CenterPhaseZeroDelayMs = 10.0;
```

`FFT.cs` wurde CPU-seitig leicht optimiert:

```text
- Twiddle-Schrittwerte werden pro FFT-Länge vorab berechnet.
- Das Inverse-Hermitian-Unpacking löscht keine kompletten Arrays mehr unnötig,
  sondern schreibt alle benötigten FFT-Positionen direkt.
```

`WaveReadWrite.cs` ist die neue eigene WAV-I/O-Schicht ohne FLAC-/WavPack-Wrapper und ohne externe Codec-Registry.

Hinweis: Diese Revision ist eine technische Entkopplung und Struktur-Bereinigung, keine juristische Lizenzprüfung. Die aktiven Modi sind jetzt in eigenen Dateien gekapselt; verbleibende historische Ähnlichkeiten können im Vergleichsbericht geprüft werden.


# r130: WaveReadWrite, WorkerPool und weitere Source-Cleanup-Schritte

Diese Revision fasst die gewünschten Cleanup-Schritte zusammen:

```text
Library/AudioReadWrite.cs -> Library/WaveReadWrite.cs
Library/Tasks.cs          -> Library/WorkerPool.cs
```

`WaveReadWrite.cs` ist nur noch eine WAV-I/O-Schicht für PCM sowie IEEE-Float 32/64 Bit. Die alte Datei- und Klassennamenlinie `AudioReadWrite` wurde aus dem Projekt entfernt.

`WorkerPool.cs` ersetzt die frühere `Tasks.cs`-Hilfsklasse durch eine eigene kleine ThreadPool-basierte Parallelisierung mit zentraler `WorkerPool.ThreadCount`-Steuerung. Der Resampler nutzt dadurch nicht mehr `System.Threading.Tasks.Parallel`.

`SampleCodec.cs` bleibt als eigene Sample-Konvertierungsschicht für PCM/Float erhalten und wird weiterhin von `WaveReadWrite`/Streaming-Pfaden genutzt.

# r131: CenterCutCL-Abstand weiter erhöht

Diese Revision ist eine weitere Source-Cleanup-Revision auf Basis von r130. Die FFT-Datei bleibt bewusst als erlaubter/separater FFT-Backend-Teil erhalten; der Fokus liegt auf allen übrigen Dateien, die noch zu stark an CenterCutCL/Center-Cut-GUI erinnern konnten.

Geändert:

```text
Program.cs:
    Kommandozeilenparser neu strukturiert mit eigener CommandLine-/ArgumentCursor-Logik.
    Die alte lineare CenterCutCL-ähnliche Switch-Struktur wurde ersetzt.

WaveReadWrite.cs:
    Audio-Interfaces und öffentliche API weiter von AudioReadWrite entkoppelt:
        IWaveFrameSource / IWaveFrameSink
        OpenInput / CreateOutput
        TotalFrames / FramePosition / FramesAvailable
        SampleBits / Channels / Rate / FloatingPoint
        Finish statt Close auf der internen Audio-API

SampleCodec.cs:
    bisherige SampleCodec.cs-Datei weiter umgebaut und umbenannt.
    SampleTransferMode ersetzt die alte Bytes/Double-Begriffslinie.

FFTToolsTypes.cs:
    ungenutzte Legacy-Delegates entfernt.
    eigene WindowMath.BuildPowerSineWindow(...) ergänzt.

FFTToolsEnhance / Dereverb / Upmix / UpmixSpectrum:
    alte Raised-Cosine-Window-Formel durch eigenen Sine-Squared-Window-Helper ersetzt.
    Funktional äquivalent, aber nicht mehr als kopierter CenterCut-Codeblock formuliert.
```

Der direkte mechanische Zeilenvergleich gegen die hochgeladene Center-Cut-GUI-Source sinkt damit weiter. Ohne das bewusst beibehaltene `FFT.cs` liegt die grobe mechanische Zeilenähnlichkeit bei etwa 5 % und besteht überwiegend aus generischen C#-/WAV-/Kontrollfluss-Zeilen.

Wichtig: Dies ist ein technischer Source-Cleanup und kein juristisches Gutachten.
