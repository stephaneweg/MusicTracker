# -*- coding: utf-8 -*-
# Hand-built cheerful/bouncy Ghibli piece (NOT via catalogue): skipping dotted rhythms, high bright register,
# Zigzag contour, pizzicato oom-pah. D major with add9/sus sparkle. Uses RegisterShift/Contour/Anchor.
import json, uuid
SPQ=24; MSPQ=4; BPB=4; BAR=BPB*SPQ
def gid(): return str(uuid.uuid4())

CH={
 'Dadd9':dict(Root=2,Quality=13,Degree=0,DiatonicColour=4,Suspension=0),
 'D':dict(Root=2,Quality=0,Degree=0,DiatonicColour=0,Suspension=0),
 'A':dict(Root=9,Quality=0,Degree=4,DiatonicColour=0,Suspension=0),
 'Asus4':dict(Root=9,Quality=5,Degree=4,DiatonicColour=0,Suspension=2),
 'A7':dict(Root=9,Quality=8,Degree=4,DiatonicColour=2,Suspension=0),
 'Bm':dict(Root=11,Quality=1,Degree=5,DiatonicColour=0,Suspension=0),
 'Bm7':dict(Root=11,Quality=7,Degree=5,DiatonicColour=2,Suspension=0),
 'G':dict(Root=7,Quality=0,Degree=3,DiatonicColour=0,Suspension=0),
 'Gadd9':dict(Root=7,Quality=13,Degree=3,DiatonicColour=4,Suspension=0),
 'Em7':dict(Root=4,Quality=7,Degree=1,DiatonicColour=2,Suspension=0),
 'Fsharpm':dict(Root=6,Quality=1,Degree=2,DiatonicColour=0,Suspension=0),
}
# (name,role,chords,mel_pattern,reg,contour,anchor,counter_contour)
SECTIONS=[
 ('Intro','intro',   ['Dadd9','Dadd9','Gadd9','Asus4'],
    ['perk','hop','perk','end'], 3, 0, 3, 4),
 ('ThemeA','theme',  ['Dadd9','A','Bm','G','D','G','Asus4','Dadd9'],
    ['skip','bounce','skip','light','skip','bounce','hop','end'], 3, 4, 2, 4),
 ('ThemeB','theme',  ['Gadd9','D','Em7','A','Bm7','G','Asus4','D'],
    ['bounce','hop','bounce','light','bounce','hop','perk','end'], 2, 0, 3, 0),
 ('Dev','dev',       ['Em7','A','Fsharpm','Bm','Em7','A7','D','A7'],
    ['skip','run','skip','run','skip','run','perk','end'], 5, 1, 1, 4),
 ('Recap','reexpo',  ['Dadd9','A','Bm','G','D','G','Asus4','Dadd9'],
    ['skip','bounce','skip','light','skip','bounce','hop','end'], 3, 4, 2, 4),
 ('Outro','outro',   ['Gadd9','Asus4','Dadd9','Dadd9'],
    ['hop','end','light','end'], 0, 2, 1, 2),
]
BARS=[]
for s in SECTIONS: BARS+=s[2]
TOTAL=len(BARS)

d16=.25; d8dot=.75
P={
 'skip':[(0,d8dot),(d8dot,d16),(1,d8dot),(1.75,d16),(2,d8dot),(2.75,d16),(3,d8dot),(3.75,d16)],
 'bounce':[(0,.5),(.5,.5),(1,d8dot),(1.75,d16),(2,.5),(2.5,.5),(3,d8dot),(3.75,d16)],
 'hop':[(0,d8dot),(.75,d16),(1,1),(2,d8dot),(2.75,d16),(3,1)],
 'perk':[(0,.5),(.5,d16),(.75,d16),(1,.5),(1.5,.5),(2,d8dot),(2.75,d16),(3,1)],
 'light':[(0,.5),(.5,.5),(1,.5),(2,.5),(2.5,.5),(3,.5)],
 'run':[(i*.5,.5) for i in range(8)],
 'end':[(0,1),(1,.5),(1.5,.5),(2,2)],
 'ct':[(1,.5),(1.5,.5),(3,.5),(3.5,.5)],
 'bass2':[(0,.5),(2,.5)],
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

E=12;Q=24
OOMPAH=[(0,0,E),(1,Q,E),(2,Q,E),(3,Q,E),(0,2*Q,E),(1,3*Q,E),(2,3*Q,E),(3,3*Q,E)]  # bass 1 · stab 2 · bass 3 · stab 4
def module_pat(pg):
    d={"$type":"Pattern"};d.update(pg);d.update(dict(Id=gid(),X=0,Y=0,WidthHint=0,Collapsed=False));return d
def vl_inv(name,prev):
    c=CH[name];q=c['Quality']
    third=3 if q in(1,2,7,9,14) else 4; fifth=6 if q in(2,9) else 7
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
      Suspension=c['Suspension'],ModeOverride=0,OpenVoicing=False,Style=28,BeatsPerBar=BPB,Repeats=1,
      CustomSlices=slices_from_notes(OOMPAH,BAR),CustomSlicesPerQuarter=SPQ,CustomNotes=[rnote(r,s,l) for(r,s,l) in OOMPAH],
      UserStyleName=user,MelodicSlices=None,MelodicSlicesPerQuarter=4,MelodicNotes=None,MelodicOctave=5,MelodicAnchor=0,
      MelodicOpenVoicing=False,MelodicVoiceLead=0,MelodicPreserve=False))

mel=[];ct=[];bass=[];chords_it=[];prev=[None]
for (name,role,chs,mp,reg,cont,anc,ctc) in SECTIONS:
    nb=len(chs)
    mel.append(dict(SilenceBefore=0,Module=line_module(name,mp,reg,cont,anc),Repeat=None))
    ct.append(dict(SilenceBefore=0,Module=line_module('Ct '+name,['ct']*nb,reg-10,ctc,3),Repeat=None))
    bass.append(dict(SilenceBefore=0,Module=line_module('Bass '+name,['bass2']*nb,-24,3,1),Repeat=None))
    for nm in chs:
        chords_it.append(dict(SilenceBefore=0,Module=chord_pat(nm,vl_inv(nm,prev),name),Repeat=None))
def track(nm,instr,items,vol): return dict(Name=nm,Type=0,Instrument=instr,Clef=None,Volume=vol,VolumeAutomation=[],Items=items)
tracks=[
 track('Mélodie (flûte)',73,mel,1.0),
 track('Contre-chant (hautbois)',68,ct,0.65),
 track('Pizzicato',45,chords_it,0.6),
 track('Basse',42,bass,0.6),
]
arr_chords=[dict(Root=CH[nm]['Root'],Quality=CH[nm]['Quality']) for nm in BARS]
arr_sections=[];sb=0
for (name,role,chs,*_) in SECTIONS:
    arr_sections.append(dict(Name=name,Role=role,StartBar=sb,Bars=len(chs),
      MelodyRiffId="00000000-0000-0000-0000-000000000000",CounterRiffId="00000000-0000-0000-0000-000000000000",Protected=False));sb+=len(chs)
arrangement=dict(Composer="Claude",ModelFile="",Seed=0,TonicPc=2,FullMode=0,MeterNum=4,MeterDen=4,BarSlices=BAR,
  SlicesPerQuarter=SPQ,ChordsPerBar=1,ChordSlices=BAR,TotalBars=TOTAL,ThemeBars=8,
  ThemeRiffId="00000000-0000-0000-0000-000000000000",Motif=None,OpenVoicing=False,Feel=1,Ternary=False,
  MelodyCenter=76,LeadInstrument=73,CounterInstrument=68,Chords=arr_chords,Sections=arr_sections,
  Options={"form":0,"style":0,"mode":0,"char":0},Theme=[],BassMotifs={},Motifs={},DevKeys=[])
project=dict(UserChordStyles=[],UserMelodicLines=[],Key=dict(FullMode=0,TonicLetter=1,Accidental=0,Mode=0),
  TimeSigNum=4,TimeSigDen=4,PickupBeats=0,TimeSigScale=1.0,MainBpm=126,Tempo=[dict(Beat=0,Bpm=126)],Tracks=tracks,Arrangement=arrangement)
out=r"C:\Users\swe\Desktop\ghibli_perky.sq"
json.dump(dict(Project=project,Riffs=[]),open(out,'w',encoding='utf-8'),ensure_ascii=False)
json.load(open(out,encoding='utf-8'))
print("wrote",out,"bars=",TOTAL,"bpm=126")
print("envelope:",[s[4] for s in SECTIONS],"contours:",[s[5] for s in SECTIONS])
