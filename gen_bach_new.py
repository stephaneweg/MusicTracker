# -*- coding: utf-8 -*-
# Hand-built Bach-style piece (NOT via the catalogue): moto perpetuo melodic lines, imitative counter, walking bass,
# circle-of-fifths 7th sequences, D harmonic minor. Uses the new MelodicLine features (RegisterShift/Contour/Anchor).
import json, uuid
SPQ=24; MSPQ=4; BPB=4; BAR=BPB*SPQ
def gid(): return str(uuid.uuid4())

# D harmonic minor. i=Dm ii0=Edim/Em7b5 III=F iv=Gm V=A(major, C#) VI=Bb VII=C
CH={
 'Dm':dict(Root=2,Quality=1,Degree=0,DiatonicColour=0,Suspension=0),
 'Dm7':dict(Root=2,Quality=7,Degree=0,DiatonicColour=2,Suspension=0),
 'Gm':dict(Root=7,Quality=1,Degree=3,DiatonicColour=0,Suspension=0),
 'Gm7':dict(Root=7,Quality=7,Degree=3,DiatonicColour=2,Suspension=0),
 'C':dict(Root=0,Quality=0,Degree=-1,DiatonicColour=0,Suspension=0),
 'Cmaj7':dict(Root=0,Quality=6,Degree=-1,DiatonicColour=2,Suspension=0),
 'F':dict(Root=5,Quality=0,Degree=2,DiatonicColour=0,Suspension=0),
 'Fmaj7':dict(Root=5,Quality=6,Degree=2,DiatonicColour=2,Suspension=0),
 'Bb':dict(Root=10,Quality=0,Degree=5,DiatonicColour=0,Suspension=0),
 'Bbmaj7':dict(Root=10,Quality=6,Degree=5,DiatonicColour=2,Suspension=0),
 'Em7b5':dict(Root=4,Quality=9,Degree=1,DiatonicColour=2,Suspension=0),  # ii ø7
 'A':dict(Root=9,Quality=0,Degree=4,DiatonicColour=0,Suspension=0,Mode=5),        # V (major, C#)
 'A7':dict(Root=9,Quality=8,Degree=4,DiatonicColour=2,Suspension=0,Mode=5),       # V7
}

# (name, role, chords, mel_pattern, reg, contour, anchor, counter_contour)
# contour: 0 Vague 1 Montante 2 Descendante 3 Statique 4 Zigzag 6 Thue-Morse 7 L-systeme 8 Fractale
# anchor: 0 def 1 fond 2 tierce 3 quinte 4 7e 5 9e
SECTIONS=[
 ('Exposition','theme', ['Dm','Gm','C','A7','Dm','Gm','A7','Dm'],
    ['sub','run8','mix','run8','sub','run8','mix','cad'], 0, 0, 1, 0),
 ('Episode','dev',      ['Dm7','Gm7','Cmaj7','Fmaj7','Bbmaj7','Em7b5','A7','Dm'],   # descending-fifths 7ths
    ['run8','run8','run8','run8','run8','run8','run8','cad'], 2, 7, 3, 2),
 ('Developpement','dev',['F','C','F','Bb','Gm','A7','Dm','A7'],
    ['run16','run8','run16','run8','run16','run8','mix','cad'], 4, 1, 4, 0),
 ('Retour','reexpo',    ['Dm','Gm','Cmaj7','F','Bb','Gm','A7','Dm'],
    ['sub','run8','mix','run8','sub','run8','mix','cad'], 0, 0, 1, 0),
 ('Coda','outro',       ['Gm','A7','Dm','Dm'],
    ['run8','cad','cad','cad'], -2, 2, 1, 2),
]
BARS=[]
for s in SECTIONS: BARS+=s[2]
TOTAL=len(BARS)

P={
 'sub':[(0,.5),(.5,.25),(.75,.25),(1,.5),(1.5,.5),(2,.5),(2.5,.5),(3,.5),(3.5,.5)],
 'run8':[(i*.5,.5) for i in range(8)],
 'run16':[(i*.25,.25) for i in range(16)],
 'mix':[(0,.5),(.5,.25),(.75,.25),(1,.5),(1.5,.5),(2,.5),(2.5,.25),(2.75,.25),(3,1)],
 'cad':[(0,.5),(.5,.5),(1,.5),(1.5,.5),(2,2)],
 'walk':[(0,1),(1,1),(2,1),(3,1)],
 'ct8':[(i*.5,.5) for i in range(8)],
}
def slices_from_notes(notes,total):
    lo=[0]*total
    for row,st,ln in notes:
        for s in range(st,min(st+ln,total)):
            if 0<=row<32: lo[s]|=(1<<row)
    return [dict(NotesLow=lo[s],NotesHigh=0) for s in range(total)]
def rnote(row,start,length,voice=0): return dict(End=start+length,Note=row,Start=start,Length=length,Bend=None,Voice=voice)
def mel_notes(keys):
    out=[]
    for bi,pk in enumerate(keys):
        base=bi*BPB
        for (st,ln) in P[pk]:
            out.append(rnote(0,int(round((base+st)*MSPQ)),max(1,int(round(ln*MSPQ))),0))
    return out
def line_module(name,keys,reg,cont,anc):
    tb=len(keys)*BPB; notes=mel_notes(keys)
    return {"$type":"MelodicLine","Preserve":False,"BeatsPerBar":tb,"VoiceCount":1,"LineName":name,
      "Slices":slices_from_notes([(n['Note'],n['Start'],n['Length']) for n in notes],tb*MSPQ),
      "SlicesPerQuarter":MSPQ,"Notes":notes,"RegisterShift":reg,"Contour":cont,"Anchor":anc,
      "Id":gid(),"X":0,"Y":0,"WidthHint":0,"Collapsed":False}

CONT=[(0,0,24),(1,0,12),(2,12,12),(3,24,12),(2,36,12),(1,48,12),(2,60,12),(3,72,12),(2,84,12)]  # eighth broken-chord continuo
def module_pat(pg):
    d={"$type":"Pattern"};d.update(pg);d.update(dict(Id=gid(),X=0,Y=0,WidthHint=0,Collapsed=False));return d
def vl_inv(name,prev):
    c=CH[name];q=c['Quality']
    third=3 if q in(1,2,7,9) else 4; fifth=6 if q in(2,9) else 7
    tones=sorted([c['Root']%12,(c['Root']+third)%12,(c['Root']+fifth)%12])
    best,bc,bl=0,999,tones[0]
    for inv in range(3):
        low=tones[inv%3];cost=0 if prev[0] is None else min((low-prev[0])%12,(prev[0]-low)%12)
        if cost<bc:bc=cost;best=inv;bl=low
    prev[0]=bl;return best
def chord_pat(name,inv,user):
    c=CH[name]
    return module_pat(dict(Root=c['Root'],Degree=c['Degree'],Bass=False,BassPerBeat=False,HeldMode=0,ClimbMode=0,
      HalveDurations=False,Octave=4,Quality=c['Quality'],Inversion=inv,VoiceLeadMode=1,DiatonicColour=c['DiatonicColour'],
      Suspension=c['Suspension'],ModeOverride=c.get('Mode',0),OpenVoicing=False,Style=28,BeatsPerBar=BPB,Repeats=1,
      CustomSlices=slices_from_notes(CONT,BAR),CustomSlicesPerQuarter=SPQ,CustomNotes=[rnote(r,s,l) for(r,s,l) in CONT],
      UserStyleName=user,MelodicSlices=None,MelodicSlicesPerQuarter=4,MelodicNotes=None,MelodicOctave=5,MelodicAnchor=0,
      MelodicOpenVoicing=False,MelodicVoiceLead=0,MelodicPreserve=False))

mel=[];ct=[];bass=[];chords_it=[];prev=[None]
for (name,role,chs,mp,reg,cont,anc,ctc) in SECTIONS:
    nb=len(chs)
    mel.append(dict(SilenceBefore=0,Module=line_module(name,mp,reg,cont,anc),Repeat=None))
    ct.append(dict(SilenceBefore=0,Module=line_module('Ct '+name,['ct8']*nb,reg-12,ctc,3),Repeat=None))   # counter: running eighths, octave below
    bass.append(dict(SilenceBefore=0,Module=line_module('Bass '+name,['walk']*nb,-24,0,1),Repeat=None))    # walking bass, Vague
    for nm in chs:
        chords_it.append(dict(SilenceBefore=0,Module=chord_pat(nm,vl_inv(nm,prev),name),Repeat=None))

def track(nm,instr,items,vol): return dict(Name=nm,Type=0,Instrument=instr,Clef=None,Volume=vol,VolumeAutomation=[],Items=items)
tracks=[
 track('Sujet (violon)',40,mel,1.0),
 track('Contre-sujet (hautbois)',68,ct,0.8),
 track('Continuo (clavecin)',6,chords_it,0.6),
 track('Basse (violoncelle)',42,bass,0.7),
]
arr_chords=[dict(Root=CH[nm]['Root'],Quality=CH[nm]['Quality']) for nm in BARS]
arr_sections=[];sb=0
for (name,role,chs,*_) in SECTIONS:
    arr_sections.append(dict(Name=name,Role=role,StartBar=sb,Bars=len(chs),
      MelodyRiffId="00000000-0000-0000-0000-000000000000",CounterRiffId="00000000-0000-0000-0000-000000000000",Protected=False));sb+=len(chs)
arrangement=dict(Composer="Claude",ModelFile="",Seed=0,TonicPc=2,FullMode=2,MeterNum=4,MeterDen=4,BarSlices=BAR,
  SlicesPerQuarter=SPQ,ChordsPerBar=1,ChordSlices=BAR,TotalBars=TOTAL,ThemeBars=8,
  ThemeRiffId="00000000-0000-0000-0000-000000000000",Motif=None,OpenVoicing=False,Feel=1,Ternary=False,
  MelodyCenter=72,LeadInstrument=40,CounterInstrument=68,Chords=arr_chords,Sections=arr_sections,
  Options={"form":0,"style":0,"mode":1,"char":0},Theme=[],BassMotifs={},Motifs={},DevKeys=[])
project=dict(UserChordStyles=[],UserMelodicLines=[],Key=dict(FullMode=2,TonicLetter=1,Accidental=0,Mode=1),
  TimeSigNum=4,TimeSigDen=4,PickupBeats=0,TimeSigScale=1.0,MainBpm=96,Tempo=[dict(Beat=0,Bpm=96)],Tracks=tracks,Arrangement=arrangement)
out=r"C:\Users\swe\Desktop\bach_new.sq"
json.dump(dict(Project=project,Riffs=[]),open(out,'w',encoding='utf-8'),ensure_ascii=False)
json.load(open(out,encoding='utf-8'))
print("wrote",out,"bars=",TOTAL)
print("envelope:",[s[4] for s in SECTIONS],"contours:",[s[5] for s in SECTIONS],"anchors:",[s[6] for s in SECTIONS])
