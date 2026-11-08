namespace Person_Movie_Player.Services;

public interface IUiDispatcher
{
    void Post(Action action);
    Task YieldAsync();
}
