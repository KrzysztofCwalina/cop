namespace SampleApp;

public class UserService
{
    public void CreateUser(string name) { }
    public void DeleteUser(int id) { }
    public string GetUserName(int id) => "";
}

public class OrderService
{
    public void PlaceOrder(string product, int quantity) { }
    public void CancelOrder(int orderId) { }
}

public interface IRepository<T>
{
    T GetById(int id);
    void Save(T entity);
}