# Playback fixtures

`tracks.mkv` is a generated 20-second fixture: a solid navy H.264 picture, two AAC sine-wave tracks tagged Russian and English, and one English SRT subtitle track. It contains no film footage or third-party content. `sample.srt` contains the generated subtitle text. The fixture is about 66 KB and requires no network or encoder at test time.

The first audio track is the default Russian dub; the second is tagged English original. Native integration tests switch between their actual VLC track IDs, toggle embedded subtitles and Off, add an external subtitle, switch files, and verify database-backed resume after replacing the service container and player. Dummy outputs and software decoding keep these tests independent of audio devices and GPU rendering.

The additional generated fixtures derive from the same solid picture and sine-wave audio:

| File | Container | Video / audio | Generation |
|---|---|---|---|
| `sample.mp4` | MP4 | H.264 / AAC | VLC remux of `tracks.mkv`, excluding subtitles |
| `sample.mov` | QuickTime | H.264 / AAC | VLC MOV remux, excluding subtitles |
| `sample.avi` | AVI | MPEG-4 Visual / MP3 | VLC transcode with `vcodec=mp4v,vb=80,acodec=mp3,ab=32` |
| `sample.wmv` | ASF | WMV2 / WMA2 | VLC transcode with `vcodec=WMV2,vb=80,acodec=wma2,ab=64` |

These fixtures total less than 1 MB. They contain no third-party footage. Tests require actual decoded video frames before checking seeking, speed, and pause/resume; renaming a file extension does not provide format coverage. No transcoder is needed at test time. Broader codecs, WebM, transport streams, hardware output, and audio-device coverage remain release checks.
