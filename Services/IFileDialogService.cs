namespace Person_Movie_Player.Services;

public interface IFileDialogService
{
    string? ChooseVideoFile();
    string? ChooseSubtitleFile();
    string? ChoosePosterFile() => null;
    ResumeChoice ChooseResume(string title, long positionMs);
}

public enum ResumeChoice { Resume, StartOver, Cancel }
