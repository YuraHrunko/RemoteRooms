#include "vendor/json.hpp"
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <random>
#include <string>
#include <vector>

using nlohmann::json;
namespace fs = std::filesystem;

static std::mutex gate;
static fs::path file_path;
static json data;
static std::vector<json> undo_stack, redo_stack;
static std::string response;
static std::string error_text;

static json empty_data() {
    return {{"version", 1}, {"rooms", json::array()}, {"desks", json::array()},
            {"computers", json::array()}, {"endpoints", json::array()}};
}

static std::string new_id() {
    static std::mt19937_64 rng{std::random_device{}()};
    char text[33];
    auto a = rng(), b = rng();
    snprintf(text, sizeof(text), "%016llx%016llx", (unsigned long long)a, (unsigned long long)b);
    return text;
}

static bool persist() {
    try {
        fs::create_directories(file_path.parent_path());
        fs::path temp = file_path;
        temp += L".tmp";
        std::ofstream out(temp, std::ios::binary | std::ios::trunc);
        if (!out) throw std::runtime_error("Cannot open data file");
        out << data.dump(2);
        out.close();
        if (!out) throw std::runtime_error("Cannot write data file");
        if (!MoveFileExW(temp.c_str(), file_path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
            throw std::runtime_error("Cannot replace data file");
        return true;
    } catch (const std::exception& ex) { error_text = ex.what(); return false; }
}

static bool exists(const char* collection, const std::string& id) {
    for (const auto& item : data[collection]) if (item.value("id", "") == id) return true;
    return false;
}

static json* find_item(const std::string& collection, const std::string& id) {
    for (auto& item : data[collection]) if (item.value("id", "") == id) return &item;
    return nullptr;
}

static void erase_item(const char* collection, const std::string& id) {
    auto& arr = data[collection];
    for (auto it = arr.begin(); it != arr.end();) {
        if (it->value("id", "") == id) it = arr.erase(it); else ++it;
    }
}

static void apply(const json& cmd) {
    const std::string op = cmd.at("op").get<std::string>();
    if (op == "addRoom") {
        data["rooms"].push_back({{"id", new_id()}, {"name", cmd.value("name", "Нова кімната")},
            {"width", 900}, {"height", 600}});
    } else if (op == "addDesk" || op == "addComputer") {
        const std::string room = cmd.at("roomId").get<std::string>();
        if (!exists("rooms", room)) throw std::runtime_error("Room does not exist");
        json item = {{"id", new_id()}, {"roomId", room}, {"x", cmd.value("x", 80.0)},
                     {"y", cmd.value("y", 80.0)}};
        if (op == "addDesk") {
            item["name"] = cmd.value("name", "Стіл");
            item["width"] = 120;
            item["height"] = 64;
            data["desks"].push_back(item);
        } else {
            item["name"] = cmd.value("name", "Новий ПК");
            data["computers"].push_back(item);
        }
    } else if (op == "addEndpoint") {
        const std::string pc = cmd.at("computerId").get<std::string>();
        const std::string protocol = cmd.at("protocol").get<std::string>();
        if (!exists("computers", pc)) throw std::runtime_error("Computer does not exist");
        if (protocol != "RDP" && protocol != "VNC" && protocol != "SSH") throw std::runtime_error("Invalid protocol");
        auto host = cmd.at("host").get<std::string>();
        if (host.empty()) throw std::runtime_error("Host is required");
        int port = cmd.value("port", protocol == "RDP" ? 3389 : protocol == "VNC" ? 5900 : 22);
        if (port < 1 || port > 65535) throw std::runtime_error("Invalid port");
        data["endpoints"].push_back({{"id", new_id()}, {"computerId", pc}, {"protocol", protocol},
            {"host", host}, {"port", port}, {"username", cmd.value("username", "")},
            {"name", cmd.value("name", protocol)}});
    } else if (op == "set") {
        const std::string collection = cmd.at("collection").get<std::string>();
        if (collection != "rooms" && collection != "desks" && collection != "computers" && collection != "endpoints")
            throw std::runtime_error("Invalid collection");
        auto* item = find_item(collection, cmd.at("id").get<std::string>());
        if (!item) throw std::runtime_error("Item does not exist");
        const std::string field = cmd.at("field").get<std::string>();
        bool allowed = field == "name" || (collection == "endpoints" &&
            (field == "host" || field == "port" || field == "username")) ||
            (collection == "rooms" && (field == "width" || field == "height"));
        if (!allowed) throw std::runtime_error("Invalid field");
        (*item)[field] = cmd.at("value");
    } else if (op == "move") {
        const std::string collection = cmd.at("collection").get<std::string>();
        if (collection != "desks" && collection != "computers") throw std::runtime_error("Invalid collection");
        auto* item = find_item(collection, cmd.at("id").get<std::string>());
        if (!item) throw std::runtime_error("Item does not exist");
        double x = cmd.at("x").get<double>(), y = cmd.at("y").get<double>();
        if (x < 0 || y < 0 || x > 10000 || y > 10000) throw std::runtime_error("Invalid position");
        (*item)["x"] = x; (*item)["y"] = y;
    } else if (op == "delete") {
        const std::string collection = cmd.at("collection").get<std::string>();
        const std::string id = cmd.at("id").get<std::string>();
        if (collection == "rooms") {
            std::vector<std::string> pcs;
            for (const auto& pc : data["computers"]) if (pc.value("roomId", "") == id) pcs.push_back(pc.value("id", ""));
            for (const auto& pc : pcs) {
                for (auto it = data["endpoints"].begin(); it != data["endpoints"].end();)
                    if (it->value("computerId", "") == pc) it = data["endpoints"].erase(it); else ++it;
                erase_item("computers", pc);
            }
            for (auto it = data["desks"].begin(); it != data["desks"].end();)
                if (it->value("roomId", "") == id) it = data["desks"].erase(it); else ++it;
            erase_item("rooms", id);
        } else if (collection == "computers") {
            for (auto it = data["endpoints"].begin(); it != data["endpoints"].end();)
                if (it->value("computerId", "") == id) it = data["endpoints"].erase(it); else ++it;
            erase_item("computers", id);
        } else if (collection == "desks" || collection == "endpoints") erase_item(collection.c_str(), id);
        else throw std::runtime_error("Invalid collection");
    } else throw std::runtime_error("Unknown operation");
}

extern "C" __declspec(dllexport) int __cdecl rr_open(const wchar_t* path) {
    std::lock_guard<std::mutex> lock(gate);
    try {
        file_path = path;
        if (file_path.empty()) throw std::runtime_error("Missing path");
        if (fs::exists(file_path)) { std::ifstream in(file_path, std::ios::binary); in >> data; }
        else data = empty_data();
        for (const char* key : {"rooms", "desks", "computers", "endpoints"})
            if (!data.contains(key) || !data[key].is_array()) throw std::runtime_error("Invalid data file");
        undo_stack.clear(); redo_stack.clear(); error_text.clear();
        return 1;
    } catch (const std::exception& ex) { error_text = ex.what(); return 0; }
}

extern "C" __declspec(dllexport) const char* __cdecl rr_state() {
    std::lock_guard<std::mutex> lock(gate);
    response = data.dump();
    return response.c_str();
}

extern "C" __declspec(dllexport) const char* __cdecl rr_error() { return error_text.c_str(); }

extern "C" __declspec(dllexport) int __cdecl rr_apply(const char* utf8) {
    std::lock_guard<std::mutex> lock(gate);
    json previous = data;
    try {
        apply(json::parse(utf8));
        if (!persist()) { data = previous; return 0; }
        undo_stack.push_back(std::move(previous));
        if (undo_stack.size() > 50) undo_stack.erase(undo_stack.begin());
        redo_stack.clear(); error_text.clear(); return 1;
    } catch (const std::exception& ex) { data = std::move(previous); error_text = ex.what(); return 0; }
}

extern "C" __declspec(dllexport) int __cdecl rr_undo() {
    std::lock_guard<std::mutex> lock(gate);
    if (undo_stack.empty()) return 0;
    json previous = data; data = undo_stack.back(); undo_stack.pop_back();
    if (!persist()) { data = previous; return 0; }
    redo_stack.push_back(std::move(previous)); return 1;
}

extern "C" __declspec(dllexport) int __cdecl rr_redo() {
    std::lock_guard<std::mutex> lock(gate);
    if (redo_stack.empty()) return 0;
    json previous = data; data = redo_stack.back(); redo_stack.pop_back();
    if (!persist()) { data = previous; return 0; }
    undo_stack.push_back(std::move(previous)); return 1;
}
