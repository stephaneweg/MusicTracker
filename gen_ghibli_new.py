# -*- coding: utf-8 -*-
# Hand-built Ghibli piece (NOT via the catalogue): melody/counter/bass = MelodicLineModule with the new
# RegisterShift / Contour / Anchor features; chords + pad = custom-articulated Patterns; arrangement grid drives it.
import json, uuid

SPQ = 24            # chords/arrangement slices per beat
MSPQ = 4            # melodic-line rhythm slices per beat
BPB = 4             # 4/4
BAR = BPB * SPQ     # 96
def gid(): return str(uuid.uuid4())

# ---------- chords (F major, Ghibli colours) ----------
CH = {
 'Fadd9':  dict(Root=5,  Quality=13, Degree=0, DiatonicColour=4, Suspension=0),
 'Fmaj7':  dict(Root=5,  Quality=6,  Degree=0, DiatonicColour=2, Suspension=0),
 'Gm7':    dict(Root=7,  Quality=7,  Degree=1, DiatonicColour=2, Suspension=0),
 'Am7':    dict(Root=9,  Quality=7,  Degree=2, DiatonicColour=2, Suspension=0),
 'Bbmaj7': dict(Root=10, Quality=6,  Degree=3, DiatonicColour=2, Suspension=0),
 'C':      dict(Root=0,  Quality=0,  Degree=4, DiatonicColour=0, Suspension=0),
 'Csus4':  dict(Root=0,  Quality=5,  Degree=4, DiatonicColour=0, Suspension=2),
 'Dm7':    dict(Root=2,  Quality=7,  Degree=5, DiatonicColour=2, Suspension=0),
 'Eb':     dict(Root=3,  Quality=0,  Degree=-1,DiatonicColour=0, Suspension=0),   # bVII
 'Db':     dict(Root=1,  Quality=0,  Degree=-1,DiatonicColour=0, Suspension=0),   # bVI
 'Ab':     dict(Root=8,  Quality=0,  Degree=-1,DiatonicColour=0, Suspension=0),   # bIII
}

# section: (name, role, chords, mel_pattern_keys, mel_register, mel_contour, mel_anchor, counter_contour)
# contour: 0 Vague 1 Montante 2 Descendante 3 Statique 4 Zigzag 5 Aleatoire 6 Thue-Morse 7 L-systeme 8 Fractale
# anchor : 0 defaut 1 fond 2 tierce 3 quinte 4 7e 5 9e
SECTIONS = [
 ('Intro','intro',   ['Fadd9','Fadd9','Bbmaj7','Csus4'],
    ['d','c','b','d'], 0, 0, 3, 3),
 ('Theme','theme',   ['Fadd9','Am7','Bbmaj7','C','Dm7','Bbmaj7','Csus4','Fadd9'],
    ['a','b','a','hold','a','b','e','d'], 0, 0, 2, 3),
 ('Dev1','dev',      ['Dm7','Am7','Bbmaj7','Fmaj7','Gm7','Dm7','Eb','Csus4'],
    ['a','e','a','b','a','e','run','d'], 3, 1, 3, 7),
 ('Climax','dev',    ['Bbmaj7','Db','Ab','Eb','Bbmaj7','Csus4','Dm7','Csus4'],
    ['e','run','e','b','run','e','run','d'], 7, 8, 5, 6),
 ('Recap','reexpo',  ['Fadd9','Am7','Bbmaj7','Csus4','Dm7','Bbmaj7','Csus4','Fadd9'],
    ['a','b','a','hold','a','b','e','d'], 0, 0, 1, 3),
 ('Outro','outro',   ['Bbmaj7','Fadd9','Gm7','Fadd9'],
    ['c','d','hold','d'], -3, 2, 1, 2),
]
BARS = []
for s in SECTIONS: BARS += s[2]
TOTAL = len(BARS)

# ---------- melodic rhythm cells (beats within a 4-beat bar) ----------
P = {
 'a':   [(0,1),(1,1),(2,.5),(2.5,.5),(3,1)],
 'b':   [(0,1.5),(1.5,.5),(2,1),(3,1)],
 'c':   [(0,2),(2,1),(3,1)],
 'd':   [(0,4)],
 'e':   [(0,.5),(.5,.5),(1,.5),(1.5,.5),(2,1),(3,1)],
 'run': [(i*.5,.5) for i in range(8)],
 'hold':[(0,2)],
 'bass':[(0,2),(2,2)],
 'ct':  [(1,1),(2,1)],          # counter: mid-bar entries
 'cth': [(0,3)],                # counter: sustained
}

def slices_from_notes(notes, total):
    lo=[0]*total
    for row,st,ln in notes:
        for s in range(st,min(st+ln,total)):
            if 0<=row<32: lo[s]|=(1<<row)
    return [dict(NotesLow=lo[s],NotesHigh=0) for s in range(total)]
def rnote(row,start,length,voice=0):
    return dict(End=start+length,Note=row,Start=start,Length=length,Bend=None,Voice=voice)

def mel_notes(pattern_keys, voice=0):
    notes=[]
    for bi,pk in enumerate(pattern_keys):
        base=bi*BPB
        for (st,ln) in P[pk]:
            s=int(round((base+st)*MSPQ)); l=max(1,int(round(ln*MSPQ)))
            notes.append(rnote(voice,s,l,voice))
    return notes

def melodic_module(name, pattern_keys, register, contour, anchor, voice_count=1):
    total_beats=len(pattern_keys)*BPB
    notes=mel_notes(pattern_keys,0)
    d={"$type":"MelodicLine","Preserve":False,"BeatsPerBar":total_beats,"VoiceCount":voice_count,
       "LineName":name,"Slices":slices_from_notes([(n['Note'],n['Start'],n['Length']) for n in notes],total_beats*MSPQ),
       "SlicesPerQuarter":MSPQ,"Notes":notes,"RegisterShift":register,"Contour":contour,"Anchor":anchor,
       "Id":gid(),"X":0,"Y":0,"WidthHint":0,"Collapsed":False}
    return d

def counter_module(name, nbars, contour, register):
    keys=['ct','cth']* ((nbars+1)//2)
    keys=keys[:nbars]
    notes=mel_notes(keys,0)
    total_beats=nbars*BPB
    return {"$type":"MelodicLine","Preserve":False,"BeatsPerBar":total_beats,"VoiceCount":1,
       "LineName":name,"Slices":slices_from_notes([(n['Note'],n['Start'],n['Length']) for n in notes],total_beats*MSPQ),
       "SlicesPerQuarter":MSPQ,"Notes":notes,"RegisterShift":register,"Contour":contour,"Anchor":1,
       "Id":gid(),"X":0,"Y":0,"WidthHint":0,"Collapsed":False}

def bass_module(name, nbars):
    keys=['bass']*nbars
    notes=mel_notes(keys,0)
    total_beats=nbars*BPB
    return {"$type":"MelodicLine","Preserve":False,"BeatsPerBar":total_beats,"VoiceCount":1,
       "LineName":name,"Slices":slices_from_notes([(n['Note'],n['Start'],n['Length']) for n in notes],total_beats*MSPQ),
       "SlicesPerQuarter":MSPQ,"Notes":notes,"RegisterShift":-24,"Contour":3,"Anchor":1,
       "Id":gid(),"X":0,"Y":0,"WidthHint":0,"Collapsed":False}

# ---------- chord / nappe custom articulations (spq=24, bar=96) ----------
Q=24;E=12
CHORD_ARTIC=[(0,0,2*Q),(1,0,E),(3,E,E),(5,2*Q,E),(2,2*Q+E,E),(3,3*Q,E),(5,3*Q+E,E)]  # bass + rolling harp
NAPPE_ARTIC=[(1,0,BAR),(2,0,BAR),(3,0,BAR),(5,0,BAR)]

def module_pat(pg):
    d={"$type":"Pattern"}; d.update(pg); d.update(dict(Id=gid(),X=0,Y=0,WidthHint=0,Collapsed=False)); return d

def vl_inv(name, prev):
    c=CH[name]; q=c['Quality']
    third=3 if q in (1,2,7,12,14,17) else 4
    fifth=6 if q in (2,9,10) else (8 if q==3 else 7)
    tones=sorted([c['Root']%12,(c['Root']+third)%12,(c['Root']+fifth)%12])
    best,bc,bl=0,999,tones[0]
    for inv in range(3):
        low=tones[inv%3]; cost=0 if prev[0] is None else min((low-prev[0])%12,(prev[0]-low)%12)
        if cost<bc: bc=cost;best=inv;bl=low
    prev[0]=bl; return best

def chord_pat(name, inv, artic, octave, open_v, user_style):
    c=CH[name]
    return module_pat(dict(Root=c['Root'],Degree=c['Degree'],Bass=False,BassPerBeat=False,HeldMode=0,
        ClimbMode=0,HalveDurations=False,Octave=octave,Quality=c['Quality'],Inversion=inv,VoiceLeadMode=1,
        DiatonicColour=c['DiatonicColour'],Suspension=c['Suspension'],ModeOverride=0,OpenVoicing=open_v,
        Style=28,BeatsPerBar=BPB,Repeats=1,CustomSlices=slices_from_notes(artic,BAR),CustomSlicesPerQuarter=SPQ,
        CustomNotes=[rnote(r,s,l) for (r,s,l) in artic],UserStyleName=user_style,
        MelodicSlices=None,MelodicSlicesPerQuarter=4,MelodicNotes=None,MelodicOctave=5,MelodicAnchor=0,
        MelodicOpenVoicing=False,MelodicVoiceLead=0,MelodicPreserve=False))

# ---------- build tracks ----------
mel_items=[]; ct_items=[]; bass_items=[]; chord_items=[]; nappe_items=[]
prevc=[None]; prevn=[None]
for (name,role,chs,mpat,reg,cont,anc,ct_cont) in SECTIONS:
    nb=len(chs)
    mel_items.append(dict(SilenceBefore=0,Module=melodic_module(name,mpat,reg,cont,anc),Repeat=None))
    ct_items.append(dict(SilenceBefore=0,Module=counter_module('Ct '+name,nb,ct_cont,reg-10),Repeat=None))
    bass_items.append(dict(SilenceBefore=0,Module=bass_module('Bass '+name,nb),Repeat=None))
    for nm in chs:
        chord_items.append(dict(SilenceBefore=0,Module=chord_pat(nm,vl_inv(nm,prevc),CHORD_ARTIC,4,False,name),Repeat=None))
        nappe_items.append(dict(SilenceBefore=0,Module=chord_pat(nm,vl_inv(nm,prevn),NAPPE_ARTIC,5,True,'Nappe'),Repeat=None))

def track(nm,instr,items,vol): return dict(Name=nm,Type=0,Instrument=instr,Clef=None,Volume=vol,VolumeAutomation=[],Items=items)
tracks=[
 track('Mélodie',73,mel_items,1.0),        # flute
 track('Contre-chant',71,ct_items,0.7),     # clarinet
 track('Accords (harpe)',46,chord_items,0.6),
 track('Nappe',48,nappe_items,0.15),        # strings
 track('Basse',42,bass_items,0.6),          # cello
]

# ---------- arrangement ----------
arr_chords=[dict(Root=CH[nm]['Root'],Quality=CH[nm]['Quality']) for nm in BARS]
arr_sections=[]; sb=0
for (name,role,chs,*_ ) in SECTIONS:
    arr_sections.append(dict(Name=name,Role=role,StartBar=sb,Bars=len(chs),
        MelodyRiffId="00000000-0000-0000-0000-000000000000",CounterRiffId="00000000-0000-0000-0000-000000000000",Protected=False))
    sb+=len(chs)
arrangement=dict(Composer="Claude",ModelFile="",Seed=0,TonicPc=5,FullMode=0,MeterNum=4,MeterDen=4,
    BarSlices=BAR,SlicesPerQuarter=SPQ,ChordsPerBar=1,ChordSlices=BAR,TotalBars=TOTAL,ThemeBars=8,
    ThemeRiffId="00000000-0000-0000-0000-000000000000",Motif=None,OpenVoicing=True,Feel=1,Ternary=False,
    MelodyCenter=72,LeadInstrument=73,CounterInstrument=71,Chords=arr_chords,Sections=arr_sections,
    Options={"form":0,"style":0,"mode":0,"char":0},Theme=[],BassMotifs={},Motifs={},DevKeys=[])

project=dict(UserChordStyles=[],UserMelodicLines=[],Key=dict(FullMode=0,TonicLetter=3,Accidental=0,Mode=0),
    TimeSigNum=4,TimeSigDen=4,PickupBeats=0,TimeSigScale=1.0,MainBpm=74,Tempo=[dict(Beat=0,Bpm=74)],
    Tracks=tracks,Arrangement=arrangement)
doc=dict(Project=project,Riffs=[])
out=r"C:\Users\swe\Desktop\ghibli_new.sq"
json.dump(doc,open(out,'w',encoding='utf-8'),ensure_ascii=False)
json.load(open(out,encoding='utf-8'))
print("wrote",out,"bars=",TOTAL,"sections=",len(SECTIONS))
print("register envelope:", [s[4] for s in SECTIONS], " contours:", [s[5] for s in SECTIONS], " anchors:", [s[6] for s in SECTIONS])
